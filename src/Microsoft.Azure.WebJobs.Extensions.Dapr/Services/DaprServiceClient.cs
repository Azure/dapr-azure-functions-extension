﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// ------------------------------------------------------------

namespace Microsoft.Azure.WebJobs.Extensions.Dapr.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Dapr.Utils;

    internal class DaprServiceClient : IDaprServiceClient
    {
        readonly HttpClient httpClient;
        readonly string defaultDaprAddress;

        public DaprServiceClient(
            IHttpClientFactory clientFactory,
            INameResolver nameResolver)
        {
            this.httpClient = clientFactory.CreateClient("DaprServiceClient");

            // "daprAddress" is an environment variable created by the Dapr process
            this.defaultDaprAddress = GetDefaultDaprAddress(nameResolver);
        }

        private static string GetDefaultDaprAddress(INameResolver resolver)
        {
            if (!int.TryParse(resolver.Resolve("DAPR_HTTP_PORT"), out int daprPort))
            {
                daprPort = 3500;
            }

            return $"http://localhost:{daprPort}";
        }

        private static async Task<HttpResponseMessage> DaprHttpCall(Func<Task<HttpResponseMessage>> httpCall)
        {
            try
            {
                var response = await httpCall();
                await ThrowIfDaprFailure(response);

                return response;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException socketException)
            {
                if (socketException.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw new DaprSidecarNotPresentException(HttpStatusCode.ServiceUnavailable, "ERR_DAPR_SIDECAR_DOES_NOT_EXIST", "Dapr sidecar is not present. Please follow this link (https://docs.dapr.io/operations/troubleshooting/common_issues/) to debug the issue with dapr.", ex);
                }

                throw new DaprException(HttpStatusCode.InternalServerError, "ERR_DAPR_REQUEST_FAILED", ex.Message, ex);
            }
            catch (DaprException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DaprException(HttpStatusCode.InternalServerError, "ERR_DAPR_REQUEST_FAILED", ex.Message, ex);
            }
        }

        private static async Task ThrowIfDaprFailure(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string errorCode = string.Empty;
                string errorMessage = string.Empty;

                if (response.Content != null && response.Content.Headers.ContentLength != 0)
                {
                    JsonElement daprError;

                    try
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        daprError = JsonDocument.Parse(content).RootElement;
                    }
                    catch (Exception e)
                    {
                        throw new DaprException(
                            response.StatusCode,
                            "ERR_UNKNOWN",
                            "The returned error message from Dapr Service is not a valid JSON Object.",
                            e);
                    }

                    if (daprError.TryGetProperty("message", out JsonElement errorMessageToken))
                    {
                        errorMessage = errorMessageToken.GetRawText();
                    }

                    if (daprError.TryGetProperty("errorCode", out JsonElement errorCodeToken))
                    {
                        errorCode = errorCodeToken.GetRawText();
                    }
                }

                // avoid potential overrides: specific 404 error messages can be returned from Dapr
                // ex: https://docs.dapr.io/reference/api/actors_api/#get-actor-state
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new DaprException(
                        response.StatusCode,
                        string.IsNullOrEmpty(errorCode) ? "ERR_DOES_NOT_EXIST" : errorCode,
                        string.IsNullOrEmpty(errorMessage) ? "The requested Dapr resource is not properly configured." : errorMessage);
                }

                throw new DaprException(
                    response.StatusCode,
                    string.IsNullOrEmpty(errorCode) ? "ERR_UNKNOWN" : errorCode,
                    string.IsNullOrEmpty(errorMessage) ? "No meaningful error message is returned." : errorMessage);
            }

            return;
        }

        public async Task SaveStateAsync(
            string? daprAddress,
            string? stateStore,
            IEnumerable<DaprStateRecord> values,
            CancellationToken cancellationToken)
        {
            if (stateStore == null)
            {
                throw new ArgumentNullException(nameof(stateStore));
            }

            this.EnsureDaprAddress(ref daprAddress);

            var stringContent = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(values, JsonUtils.DefaultSerializerOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            await DaprHttpCall(async () =>
            {
                var response = await this.httpClient.PostAsync(
                 $"{daprAddress}/v1.0/state/{Uri.EscapeDataString(stateStore)}",
                 stringContent,
                 cancellationToken);

                return response;
            });
        }

        public async Task<DaprStateRecord> GetStateAsync(
            string? daprAddress,
            string stateStore,
            string key,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var response = await DaprHttpCall(async () =>
            {
                var response = await this.httpClient.GetAsync(
                    $"{daprAddress}/v1.0/state/{stateStore}/{key}",
                    cancellationToken);

                return response;
            });

            Stream contentStream = await response.Content.ReadAsStreamAsync();
            string? eTag = response.Headers.ETag?.Tag;

            return new DaprStateRecord(key, contentStream, eTag);
        }

        public async Task InvokeMethodAsync(
            string? daprAddress,
            string appId,
            string methodName,
            string httpVerb,
            object? body,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var req = new HttpRequestMessage(new HttpMethod(httpVerb), $"{daprAddress}/v1.0/invoke/{appId}/method/{methodName}");
            if (body != null)
            {
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body, JsonUtils.DefaultSerializerOptions),
                    Encoding.UTF8,
                    "application/json");
            }

            await DaprHttpCall(async () =>
            {
                var response = await this.httpClient.SendAsync(req, cancellationToken);

                return response;
            });
        }

        public async Task SendToDaprBindingAsync(
            string? daprAddress,
            DaprBindingMessage message,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var stringContent = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(message, JsonUtils.DefaultSerializerOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            await DaprHttpCall(async () =>
            {
                var response = await this.httpClient.PostAsync(
                    $"{daprAddress}/v1.0/bindings/{message.BindingName}",
                    stringContent,
                    cancellationToken);

                return response;
            });
        }

        public async Task PublishEventAsync(
            string? daprAddress,
            string name,
            string topicName,
            JsonElement? payload,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var req = new HttpRequestMessage(HttpMethod.Post, $"{daprAddress}/v1.0/publish/{name}/{topicName}");
            if (payload != null)
            {
                req.Content = new StringContent(payload?.GetRawText(), Encoding.UTF8, "application/json");
            }

            await DaprHttpCall(async () =>
            {
                HttpResponseMessage response = await this.httpClient.SendAsync(req, cancellationToken);

                return response;
            });
        }

        public async Task<JsonDocument> GetSecretAsync(
            string? daprAddress,
            string secretStoreName,
            string? key,
            string? metadata,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(secretStoreName))
            {
                throw new ArgumentNullException(nameof(secretStoreName));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            this.EnsureDaprAddress(ref daprAddress);

            string metadataQuery = string.Empty;
            if (!string.IsNullOrEmpty(metadata))
            {
                metadataQuery = "?" + metadata;
            }

            var response = await DaprHttpCall(async () =>
            {
                var response = await this.httpClient.GetAsync(
                    $"{daprAddress}/v1.0/secrets/{secretStoreName}/{key}{metadataQuery}",
                    cancellationToken);

                return response;
            });

            string secretPayload = await response.Content.ReadAsStringAsync();

            // The response is always expected to be a JSON object
            return JsonDocument.Parse(secretPayload);
        }

        private void EnsureDaprAddress(ref string? daprAddress)
        {
            (daprAddress ??= this.defaultDaprAddress).TrimEnd('/');
        }

        class DaprException : Exception
        {
            public DaprException(HttpStatusCode statusCode, string errorCode, string message)
                : base(message)
            {
                this.StatusCode = statusCode;
                this.ErrorCode = errorCode;
            }

            public DaprException(HttpStatusCode statusCode, string errorCode, string message, Exception innerException)
                : base(message, innerException)
            {
                this.StatusCode = statusCode;
                this.ErrorCode = errorCode;
            }

            HttpStatusCode StatusCode { get; set; }

            string ErrorCode { get; set; }

            public override string ToString()
            {
                if (this.InnerException != null)
                {
                    return string.Format(
                        "Status Code: {0}; Error Code: {1} ; Message: {2}; Inner Exception: {3}",
                        this.StatusCode,
                        this.ErrorCode,
                        this.Message,
                        this.InnerException);
                }

                return string.Format(
                    "Status Code: {0}; Error Code: {1} ; Message: {2}",
                    this.StatusCode,
                    this.ErrorCode,
                    this.Message);
            }
        }

        class DaprSidecarNotPresentException : DaprException
        {
            public DaprSidecarNotPresentException(HttpStatusCode statusCode, string errorCode, string message)
                : base(statusCode, errorCode, message)
            {
            }

            public DaprSidecarNotPresentException(HttpStatusCode statusCode, string errorCode, string message, Exception innerException)
                : base(statusCode, errorCode, message, innerException)
            {
            }
        }
    }
}