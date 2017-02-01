﻿/*
 * Copyright 2010-2013 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;

namespace Amazon
{
    /// <summary>
    /// AWS4 protocol signer for service calls that transmit authorization in the header field "Authorization".
    /// </summary>
    public class AWS4Signer
    {
        public const bool IsIL2CPP = true;

        public const string Scheme = "AWS4";
        public const string Algorithm = "HMAC-SHA256";

        public const string AWS4AlgorithmTag = Scheme + "-" + Algorithm;

        public const string Terminator = "aws4_request";
        public static readonly byte[] TerminatorBytes = Encoding.UTF8.GetBytes(Terminator);

        public const string Credential = "Credential";
        public const string SignedHeaders = "SignedHeaders";
        public const string Signature = "Signature";

        public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        public const string StreamingBodySha256 = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";
        public const string AWSChunkedEncoding = "aws-chunked";

        static readonly Regex CompressWhitespaceRegex = new Regex("\\s+");
        const SigningAlgorithm SignerAlgorithm = SigningAlgorithm.HmacSHA256;

        private static IEnumerable<string> _headersToIgnoreWhenSigning = new HashSet<string>
        {
            HeaderKeys.XAmznTraceIdHeader
        };

        /// <summary>
        /// Calculates and signs the specified request using the AWS4 signing protocol by using the
        /// AWS account credentials given in the method parameters. The resulting signature is added
        /// to the request headers as 'Authorization'. Parameters supplied in the request, either in
        /// the resource path as a query string or in the Parameters collection must not have been
        /// uri encoded. If they have, use the SignRequest method to obtain a signature.
        /// </summary>
        /// <param name="request">
        /// The request to compute the signature for. Additional headers mandated by the AWS4 protocol
        /// ('host' and 'x-amz-date') will be added to the request before signing.
        /// </param>
        /// <param name="clientConfig">
        /// Client configuration data encompassing the service call (notably authentication
        /// region, endpoint and service name).
        /// </param>
        /// <param name="metrics">
        /// Metrics for the request
        /// </param>
        /// <param name="awsAccessKeyId">
        /// The AWS public key for the account making the service call.
        /// </param>
        /// <param name="awsSecretAccessKey">
        /// The AWS secret key for the account making the call, in clear text.
        /// </param>
        /// <exception cref="Amazon.Runtime.SignatureException">
        /// If any problems are encountered while signing the request.
        /// </exception>
        public void Sign(IRequest request,
            IClientConfig clientConfig,
            string awsAccessKeyId,
            string awsSecretAccessKey)
        {
            var signingResult = SignRequest(request, clientConfig, awsAccessKeyId, awsSecretAccessKey);
            request.Headers[HeaderKeys.AuthorizationHeader] = signingResult.ForAuthorizationHeader;
        }

        /// <summary>
        /// Calculates and signs the specified request using the AWS4 signing protocol by using the
        /// AWS account credentials given in the method parameters.
        /// </summary>
        /// <param name="request">
        /// The request to compute the signature for. Additional headers mandated by the AWS4 protocol
        /// ('host' and 'x-amz-date') will be added to the request before signing.
        /// </param>
        /// <param name="clientConfig">
        /// Client configuration data encompassing the service call (notably authentication
        /// region, endpoint and service name).
        /// </param>
        /// <param name="metrics">
        /// Metrics for the request.
        /// </param>
        /// <param name="awsAccessKeyId">
        /// The AWS public key for the account making the service call.
        /// </param>
        /// <param name="awsSecretAccessKey">
        /// The AWS secret key for the account making the call, in clear text.
        /// </param>
        /// <exception cref="Amazon.Runtime.SignatureException">
        /// If any problems are encountered while signing the request.
        /// </exception>
        /// <remarks>
        /// Parameters passed as part of the resource path should be uri-encoded prior to
        /// entry to the signer. Parameters passed in the request.Parameters collection should
        /// be not be encoded; encoding will be done for these parameters as part of the
        /// construction of the canonical request.
        /// </remarks>
        public AWS4SigningResult SignRequest(IRequest request,
            IClientConfig clientConfig,
            string awsAccessKeyId,
            string awsSecretAccessKey)
        {
            var signedAt = InitializeHeaders(request.Headers, request.Endpoint);
            var service = "mqtt";
            var region = RegionEndpoint.APSoutheast2.SystemName;

            var parametersToCanonicalize = GetParametersToCanonicalize(request);
            var canonicalParameters = CanonicalizeQueryParameters(parametersToCanonicalize);
            var bodyHash = SetRequestBodyHash(request);
            var sortedHeaders = SortAndPruneHeaders(request.Headers);

            var canonicalRequest = CanonicalizeRequest(request.Endpoint,
                request.ResourcePath,
                request.HttpMethod,
                sortedHeaders,
                canonicalParameters,
                bodyHash);

            return ComputeSignature(awsAccessKeyId,
                awsSecretAccessKey,
                region,
                signedAt,
                service,
                CanonicalizeHeaderNames(sortedHeaders),
                canonicalRequest);
        }

        #region Public Signing Helpers

        /// <summary>
        /// Sets the AWS4 mandated 'host' and 'x-amz-date' headers, returning the date/time that will
        /// be used throughout the signing process in various elements and formats.
        /// </summary>
        /// <param name="headers">The current set of headers</param>
        /// <param name="requestEndpoint"></param>
        /// <returns>Date and time used for x-amz-date, in UTC</returns>
        public static DateTime InitializeHeaders(IDictionary<string, string> headers, Uri requestEndpoint)
        {
            return InitializeHeaders(headers, requestEndpoint, AWSSDKUtils.CorrectedUtcNow);
        }

        /// <summary>
        /// Sets the AWS4 mandated 'host' and 'x-amz-date' headers, accepting and returning the date/time that will
        /// be used throughout the signing process in various elements and formats.
        /// </summary>
        /// <param name="headers">The current set of headers</param>
        /// <param name="requestEndpoint"></param>
        /// <param name="requestDateTime"></param>
        /// <returns>Date and time used for x-amz-date, in UTC</returns>
        public static DateTime InitializeHeaders(IDictionary<string, string> headers, Uri requestEndpoint,
            DateTime requestDateTime)
        {
            // clean up any prior signature in the headers if resigning
            CleanHeaders(headers);

            if (!headers.ContainsKey(HeaderKeys.HostHeader))
            {
                var hostHeader = requestEndpoint.Host;
                if (!requestEndpoint.IsDefaultPort)
                    hostHeader += ":" + requestEndpoint.Port;
                headers.Add(HeaderKeys.HostHeader, hostHeader);
            }

            var dt = requestDateTime;
            headers[HeaderKeys.XAmzDateHeader] = dt.ToString(AWSSDKUtils.ISO8601BasicDateTimeFormat,
                CultureInfo.InvariantCulture);

            return dt;
        }

        private static void CleanHeaders(IDictionary<string, string> headers)
        {
            headers.Remove(HeaderKeys.AuthorizationHeader);
            headers.Remove(HeaderKeys.XAmzContentSha256Header);

            if (headers.ContainsKey(HeaderKeys.XAmzDecodedContentLengthHeader))
            {
                headers[HeaderKeys.ContentLengthHeader] =
                    headers[HeaderKeys.XAmzDecodedContentLengthHeader];
                headers.Remove(HeaderKeys.XAmzDecodedContentLengthHeader);
            }
        }

        /// <summary>
        /// Computes and returns an AWS4 signature for the specified canonicalized request
        /// </summary>
        /// <param name="credentials"></param>
        /// <param name="region"></param>
        /// <param name="signedAt"></param>
        /// <param name="service"></param>
        /// <param name="signedHeaders"></param>
        /// <param name="canonicalRequest"></param>
        /// <returns></returns>
        public static AWS4SigningResult ComputeSignature(ImmutableCredentials credentials,
            string region,
            DateTime signedAt,
            string service,
            string signedHeaders,
            string canonicalRequest)
        {
            return ComputeSignature(credentials.AccessKey,
                credentials.SecretKey,
                region,
                signedAt,
                service,
                signedHeaders,
                canonicalRequest);
        }


        /// <summary>
        /// Computes and returns an AWS4 signature for the specified canonicalized request
        /// </summary>
        /// <param name="awsAccessKey"></param>
        /// <param name="awsSecretAccessKey"></param>
        /// <param name="region"></param>
        /// <param name="signedAt"></param>
        /// <param name="service"></param>
        /// <param name="signedHeaders"></param>
        /// <param name="canonicalRequest"></param>
        /// <returns></returns>
        public static AWS4SigningResult ComputeSignature(string awsAccessKey,
            string awsSecretAccessKey,
            string region,
            DateTime signedAt,
            string service,
            string signedHeaders,
            string canonicalRequest)
        {
            return ComputeSignature(awsAccessKey, awsSecretAccessKey, region, signedAt, service, signedHeaders,
                canonicalRequest, null);
        }

        /// <summary>
        /// Computes and returns an AWS4 signature for the specified canonicalized request
        /// </summary>
        /// <param name="awsAccessKey"></param>
        /// <param name="awsSecretAccessKey"></param>
        /// <param name="region"></param>
        /// <param name="signedAt"></param>
        /// <param name="service"></param>
        /// <param name="signedHeaders"></param>
        /// <param name="canonicalRequest"></param>
        /// <param name="metrics"></param>
        /// <returns></returns>
        public static AWS4SigningResult ComputeSignature(string awsAccessKey,
            string awsSecretAccessKey,
            string region,
            DateTime signedAt,
            string service,
            string signedHeaders,
            string canonicalRequest,
            RequestMetrics metrics)
        {
            var dateStamp = FormatDateTime(signedAt, AWSSDKUtils.ISO8601BasicDateFormat);
            var scope = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}/{3}", dateStamp, region, service,
                Terminator);

            var stringToSignBuilder = new StringBuilder();
            stringToSignBuilder.AppendFormat(CultureInfo.InvariantCulture, "{0}-{1}\n{2}\n{3}\n",
                Scheme,
                Algorithm,
                FormatDateTime(signedAt, AWSSDKUtils.ISO8601BasicDateTimeFormat),
                scope);

            var canonicalRequestHashBytes = ComputeHash(canonicalRequest);
            stringToSignBuilder.Append(AWSSDKUtils.ToHex(canonicalRequestHashBytes, true));

            if (metrics != null)
                metrics.AddProperty(Metric.StringToSign, stringToSignBuilder);

            var key = ComposeSigningKey(awsSecretAccessKey,
                region,
                dateStamp,
                service);

            var stringToSign = stringToSignBuilder.ToString();
            var signature = ComputeKeyedHash(SignerAlgorithm, key, stringToSign);
            return new AWS4SigningResult(awsAccessKey, signedAt, signedHeaders, scope, key, signature);
        }

        /// <summary>
        /// Formats the supplied date and time for use in AWS4 signing, where various formats are used.
        /// </summary>
        /// <param name="dt"></param>
        /// <param name="formatString">The required format</param>
        /// <returns>The UTC date/time in the requested format</returns>
        public static string FormatDateTime(DateTime dt, string formatString)
        {
            return dt.ToString(formatString, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Compute and return the multi-stage signing key for the request.
        /// </summary>
        /// <param name="awsSecretAccessKey">The clear-text AWS secret key, if not held in secureKey</param>
        /// <param name="region">The region in which the service request will be processed</param>
        /// <param name="date">Date of the request, in yyyyMMdd format</param>
        /// <param name="service">The name of the service being called by the request</param>
        /// <returns>Computed signing key</returns>
        public static byte[] ComposeSigningKey(string awsSecretAccessKey, string region, string date, string service)
        {
            char[] ksecret = null;

            try
            {
                ksecret = (Scheme + awsSecretAccessKey).ToCharArray();

                var hashDate = ComputeKeyedHash(SignerAlgorithm, Encoding.UTF8.GetBytes(ksecret),
                    Encoding.UTF8.GetBytes(date));
                var hashRegion = ComputeKeyedHash(SignerAlgorithm, hashDate, Encoding.UTF8.GetBytes(region));
                var hashService = ComputeKeyedHash(SignerAlgorithm, hashRegion, Encoding.UTF8.GetBytes(service));
                return ComputeKeyedHash(SignerAlgorithm, hashService, TerminatorBytes);
            }
            finally
            {
                // clean up all secrets, regardless of how initially seeded (for simplicity)
                if (ksecret != null)
                    Array.Clear(ksecret, 0, ksecret.Length);
            }
        }

        /// <summary>
        /// If the caller has already set the x-amz-content-sha256 header with a pre-computed
        /// content hash, or it is present as ContentStreamHash on the request instance, return
        /// the value to be used in request canonicalization.
        /// If not set as a header or in the request, attempt to compute a hash based on
        /// inspection of the style of the request content.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>
        /// The computed hash, whether already set in headers or computed here. Null
        /// if we were not able to compute a hash.
        /// </returns>
        public static string SetRequestBodyHash(IRequest request)
        {
            string computedContentHash = null;
            var shaHeaderPresent =
                request.Headers.TryGetValue(HeaderKeys.XAmzContentSha256Header, out computedContentHash);
            if (shaHeaderPresent && !request.UseChunkEncoding)
                return computedContentHash;

            if (request.UseChunkEncoding)
            {
                computedContentHash = StreamingBodySha256;
                if (request.Headers.ContainsKey(HeaderKeys.ContentLengthHeader))
                {
                    // substitute the originally declared content length with the true size of
                    // the data we'll upload, which is inflated with chunk metadata
                    request.Headers[HeaderKeys.XAmzDecodedContentLengthHeader] =
                        request.Headers[HeaderKeys.ContentLengthHeader];
                    var originalContentLength = long.Parse(request.Headers[HeaderKeys.ContentLengthHeader],
                        CultureInfo.InvariantCulture);
                    request.Headers[HeaderKeys.ContentLengthHeader]
                        = ChunkedUploadWrapperStream.ComputeChunkedContentLength(originalContentLength)
                            .ToString(CultureInfo.InvariantCulture);
                }

                if (request.Headers.ContainsKey(HeaderKeys.ContentEncodingHeader))
                {
                    var originalEncoding = request.Headers[HeaderKeys.ContentEncodingHeader];
                    if (!originalEncoding.Contains(AWSChunkedEncoding))
                    {
                        request.Headers[HeaderKeys.ContentEncodingHeader]
                            = string.Concat(originalEncoding, ", ", AWSChunkedEncoding);
                    }
                }
                else
                    request.Headers[HeaderKeys.ContentEncodingHeader] = AWSChunkedEncoding;
            }
            else
            {
                if (request.ContentStream != null)
                    computedContentHash = request.ComputeContentStreamHash();
                else
                {
                    byte[] payloadBytes = GetRequestPayloadBytes(request);
                    byte[] payloadHashBytes = CryptoUtilFactory.CryptoInstance.ComputeSHA256Hash(payloadBytes);
                    computedContentHash = AWSSDKUtils.ToHex(payloadHashBytes, true);
                }
            }

            if (computedContentHash != null)
            {
                if (shaHeaderPresent)
                    request.Headers[HeaderKeys.XAmzContentSha256Header] = computedContentHash;
                else
                    request.Headers.Add(HeaderKeys.XAmzContentSha256Header, computedContentHash);
            }

            return computedContentHash;
        }

        /// <summary>
        /// Returns the HMAC256 for an arbitrary blob using the specified key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] SignBlob(byte[] key, string data)
        {
            return SignBlob(key, Encoding.UTF8.GetBytes(data));
        }

        /// <summary>
        /// Returns the HMAC256 for an arbitrary blob using the specified key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] SignBlob(byte[] key, byte[] data)
        {
            return CryptoUtilFactory.CryptoInstance.HMACSignBinary(data, key, SignerAlgorithm);
        }

        /// <summary>
        /// Compute and return the hash of a data blob using the specified key
        /// </summary>
        /// <param name="algorithm">Algorithm to use for hashing</param>
        /// <param name="key">Hash key</param>
        /// <param name="data">Data blob</param>
        /// <returns>Hash of the data</returns>
        public static byte[] ComputeKeyedHash(SigningAlgorithm algorithm, byte[] key, string data)
        {
            return ComputeKeyedHash(algorithm, key, Encoding.UTF8.GetBytes(data));
        }

        /// <summary>
        /// Compute and return the hash of a data blob using the specified key
        /// </summary>
        /// <param name="algorithm">Algorithm to use for hashing</param>
        /// <param name="key">Hash key</param>
        /// <param name="data">Data blob</param>
        /// <returns>Hash of the data</returns>
        public static byte[] ComputeKeyedHash(SigningAlgorithm algorithm, byte[] key, byte[] data)
        {
            return CryptoUtilFactory.CryptoInstance.HMACSignBinary(data, key, algorithm);
        }

        /// <summary>
        /// Computes the non-keyed hash of the supplied data
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] ComputeHash(string data)
        {
            return ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        /// <summary>
        /// Computes the non-keyed hash of the supplied data
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] ComputeHash(byte[] data)
        {
            return CryptoUtilFactory.CryptoInstance.ComputeSHA256Hash(data);
        }

        #endregion

        #region Private Signing Helpers

        /// <summary>
        /// Computes and returns the canonical request
        /// </summary>
        /// <param name="endpoint">The endpoint URL</param>
        /// <param name="resourcePath">the path of the resource being operated on</param>
        /// <param name="httpMethod">The http method used for the request</param>
        /// <param name="sortedHeaders">The full request headers, sorted into canonical order</param>
        /// <param name="canonicalQueryString">The query parameters for the request</param>
        /// <param name="precomputedBodyHash">
        /// The hash of the binary request body if present. If not supplied, the routine
        /// will look for the hash as a header on the request.
        /// </param>
        /// <returns>Canonicalised request as a string</returns>
        protected static string CanonicalizeRequest(Uri endpoint,
            string resourcePath,
            string httpMethod,
            IDictionary<string, string> sortedHeaders,
            string canonicalQueryString,
            string precomputedBodyHash)
        {
            var canonicalRequest = new StringBuilder();
            canonicalRequest.AppendFormat("{0}\n", httpMethod);
            canonicalRequest.AppendFormat("{0}\n", AWSSDKUtils.CanonicalizeResourcePath(endpoint, resourcePath));
            canonicalRequest.AppendFormat("{0}\n", canonicalQueryString);

            canonicalRequest.AppendFormat("{0}\n", CanonicalizeHeaders(sortedHeaders));
            canonicalRequest.AppendFormat("{0}\n", CanonicalizeHeaderNames(sortedHeaders));

            if (precomputedBodyHash != null)
            {
                canonicalRequest.Append(precomputedBodyHash);
            }
            else
            {
                string contentHash;
                if (sortedHeaders.TryGetValue(HeaderKeys.XAmzContentSha256Header, out contentHash))
                    canonicalRequest.Append(contentHash);
            }

            return canonicalRequest.ToString();
        }

        /// <summary>
        /// Reorders the headers for the request for canonicalization.
        /// </summary>
        /// <param name="requestHeaders">The set of proposed headers for the request</param>
        /// <returns>List of headers that must be included in the signature</returns>
        /// <remarks>For AWS4 signing, all headers are considered viable for inclusion</remarks>
        protected static IDictionary<string, string> SortAndPruneHeaders(
            IEnumerable<KeyValuePair<string, string>> requestHeaders)
        {
            var sortedHeaders = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in requestHeaders)
            {
                if (_headersToIgnoreWhenSigning.Contains(header.Key))
                {
                    continue;
                }
                sortedHeaders.Add(header.Key, header.Value);
            }

            return sortedHeaders;
        }

        /// <summary>
        /// Computes the canonical headers with values for the request. Only headers included in the signature
        /// are included in the canonicalization process.
        /// </summary>
        /// <param name="sortedHeaders">All request headers, sorted into canonical order</param>
        /// <returns>Canonicalized string of headers, with the header names in lower case.</returns>
        protected static string CanonicalizeHeaders(IEnumerable<KeyValuePair<string, string>> sortedHeaders)
        {
            if (sortedHeaders == null || sortedHeaders.Count() == 0)
                return string.Empty;

            var builder = new StringBuilder();

            //incase of il2cpp scripting backend the sorted headers is broken.
            //so we order by the keys again
            foreach (var entry in sortedHeaders.OrderBy(kvp => kvp.Key.ToLowerInvariant()))
            {
                builder.Append(entry.Key.ToLowerInvariant());
                builder.Append(":");
                builder.Append(CompressSpaces(entry.Value));
                builder.Append("\n");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns the set of headers included in the signature as a flattened, ;-delimited string
        /// </summary>
        /// <param name="sortedHeaders">The headers included in the signature</param>
        /// <returns>Formatted string of header names</returns>
        protected static string CanonicalizeHeaderNames(IEnumerable<KeyValuePair<string, string>> sortedHeaders)
        {
            var builder = new StringBuilder();
            foreach (var header in sortedHeaders.OrderBy(kvp => kvp.Key.ToLowerInvariant()))
            {
                if (builder.Length > 0)
                    builder.Append(";");
                builder.Append(header.Key.ToLowerInvariant());
            }
            return builder.ToString();
        }

        /// <summary>
        /// Collects the subresource and query string parameters into one collection
        /// ready for canonicalization
        /// </summary>
        /// <param name="request">The in-flight request being signed</param>
        /// <returns>The fused set of parameters</returns>
        protected static IDictionary<string, string> GetParametersToCanonicalize(IRequest request)
        {
            var parametersToCanonicalize = new Dictionary<string, string>();

            if (request.SubResources != null && request.SubResources.Count > 0)
            {
                foreach (var subResource in request.SubResources)
                {
                    parametersToCanonicalize.Add(subResource.Key, subResource.Value);
                }
            }

            if (request.UseQueryString && request.Parameters != null && request.Parameters.Count > 0)
            {
                foreach (var queryParameter in request.Parameters.Where(queryParameter => queryParameter.Value != null))
                {
                    parametersToCanonicalize.Add(queryParameter.Key, queryParameter.Value);
                }
            }

            return parametersToCanonicalize;
        }

        protected static string CanonicalizeQueryParameters(string queryString)
        {
            return CanonicalizeQueryParameters(queryString, true);
        }

        /// <summary>
        /// Computes and returns the canonicalized query string, if query parameters have been supplied.
        /// Parameters with no value will be canonicalized as 'param='. The expectation is that parameters
        /// have not already been url encoded prior to canonicalization.
        /// </summary>
        /// <param name="queryString">The set of parameters being passed on the uri</param>
        /// <param name="uriEncodeParameters">
        /// Parameters must be uri encoded into the canonical request and by default the signer expects
        /// that the supplied collection contains non-encoded data. Set this to false if the encoding was
        /// done prior to signer entry.
        /// </param>
        /// <returns>The uri encoded query string parameters in canonical ordering</returns>
        protected static string CanonicalizeQueryParameters(string queryString, bool uriEncodeParameters)
        {
            if (string.IsNullOrEmpty(queryString))
                return string.Empty;

            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var queryParamsStart = queryString.IndexOf('?');
            var qs = queryString.Substring(++queryParamsStart);
            var subStringPos = 0;
            var index = qs.IndexOfAny(new char[] {'&', ';'}, 0);
            if (index == -1 && subStringPos < qs.Length)
                index = qs.Length;
            while (index != -1)
            {
                var token = qs.Substring(subStringPos, index - subStringPos);

                // If the next character is a space then this isn't the end of query string value
                // Content Disposition is an example of this.
                if (!(index + 1 < qs.Length && qs[index + 1] == ' '))
                {
                    var equalPos = token.IndexOf('=');
                    if (equalPos == -1)
                        queryParams.Add(token, null);
                    else
                        queryParams.Add(token.Substring(0, equalPos), token.Substring(equalPos + 1));

                    subStringPos = index + 1;
                }

                if (qs.Length <= index + 1)
                    break;

                index = qs.IndexOfAny(new char[] {'&', ';'}, index + 1);
                if (index == -1 && subStringPos < qs.Length)
                    index = qs.Length;
            }

            return CanonicalizeQueryParameters(queryParams, uriEncodeParameters);
        }

        protected static string CanonicalizeQueryParameters(IDictionary<string, string> parameters)
        {
            return CanonicalizeQueryParameters(parameters, true);
        }

        /// <summary>
        /// Computes and returns the canonicalized query string, if query parameters have been supplied.
        /// Parameters with no value will be canonicalized as 'param='. The expectation is that parameters
        /// have not already been url encoded prior to canonicalization.
        /// </summary>
        /// <param name="parameters">The set of parameters to be encoded in the query string</param>
        /// <param name="uriEncodeParameters">
        /// Parameters must be uri encoded into the canonical request and by default the signer expects
        /// that the supplied collection contains non-encoded data. Set this to false if the encoding was
        /// done prior to signer entry.
        /// </param>
        /// <returns>The uri encoded query string parameters in canonical ordering</returns>
        protected static string CanonicalizeQueryParameters(IDictionary<string, string> parameters,
            bool uriEncodeParameters)
        {
            if (parameters == null || parameters.Count == 0)
                return string.Empty;

            var canonicalQueryString = new StringBuilder();
            var queryParams = new SortedDictionary<string, string>(parameters, StringComparer.Ordinal);
            foreach (var p in queryParams)
            {
                if (canonicalQueryString.Length > 0)
                    canonicalQueryString.Append("&");
                if (uriEncodeParameters)
                {
                    if (string.IsNullOrEmpty(p.Value))
                        canonicalQueryString.AppendFormat("{0}=", AWSSDKUtils.UrlEncode(p.Key, false));
                    else
                        canonicalQueryString.AppendFormat("{0}={1}", AWSSDKUtils.UrlEncode(p.Key, false),
                            AWSSDKUtils.UrlEncode(p.Value, false));
                }
                else
                {
                    if (string.IsNullOrEmpty(p.Value))
                        canonicalQueryString.AppendFormat("{0}=", p.Key);
                    else
                        canonicalQueryString.AppendFormat("{0}={1}", p.Key, p.Value);
                }
            }

            return canonicalQueryString.ToString();
        }

        static string CompressSpaces(string data)
        {
            if (data == null || !data.Contains(" "))
                return data;

            var compressed = CompressWhitespaceRegex.Replace(data, " ");
            return compressed;
        }

        /// <summary>
        /// Returns the request parameters in the form of a query string.
        /// </summary>
        /// <param name="request">The request instance</param>
        /// <returns>Request parameters in query string format</returns>
        static byte[] GetRequestPayloadBytes(IRequest request)
        {
            if (request.Content != null)
                return request.Content;

            var content = request.UseQueryString ? string.Empty : AWSSDKUtils.GetParametersAsString(request.Parameters);
            return Encoding.UTF8.GetBytes(content);
        }

        #endregion


    }
}