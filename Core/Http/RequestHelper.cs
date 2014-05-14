﻿using System;
using System.Collections.Specialized;
using System.Net;

namespace NuGet
{
    internal static class RequestHelper
    {
        /// <summary>
        /// Keeps sending requests until a response code that doesn't require authentication happens or if
        /// the request requires authentication and the user has stopped trying to enter them (i.e. they hit cancel when they are prompted).
        /// </summary>
        internal static WebResponse GetResponse(Func<WebRequest> createRequest,
                                                Action<WebRequest> prepareRequest,
                                                IProxyCache proxyCache,
                                                ICredentialCache credentialCache,
                                                ICredentialProvider credentialProvider)
        {
            IHttpWebResponse previousResponse = null;
            HttpStatusCode? previousStatusCode = null;
            bool usingSTSAuth = false;
            bool continueIfFailed = true;
            int proxyCredentialsRetryCount = 0;
            int credentialsRetryCount = 0;

            while (true)
            {
                // Create the request
                var request = (HttpWebRequest)createRequest();
                request.Proxy = proxyCache.GetProxy(request.RequestUri);
                if (request.Proxy != null && request.Proxy.Credentials == null)
                {
                    request.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }

                if (previousResponse == null)
                {
                    // Try to use the cached credentials (if any, for the first request)
                    request.Credentials = credentialCache.GetCredentials(request.RequestUri);

                    // If there are no cached credentials, use the default ones
                    if (request.Credentials == null)
                    {
                        request.UseDefaultCredentials = true;
                    }

                }
                else if (previousStatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    request.Proxy.Credentials = credentialProvider.GetCredentials(request, CredentialType.ProxyCredentials, retrying: proxyCredentialsRetryCount > 0);

                    continueIfFailed = request.Proxy.Credentials != null;

                    proxyCredentialsRetryCount++;
                }
                else if ((previousStatusCode == HttpStatusCode.Unauthorized) && !usingSTSAuth)
                {
                    // If we are using STS, the auth's being performed by a request header. We do not need to ask the user for credentials at this point.
                    request.Credentials = credentialProvider.GetCredentials(request, CredentialType.RequestCredentials, retrying: credentialsRetryCount > 0);

                    continueIfFailed = request.Credentials != null;

                    credentialsRetryCount++;
                }

                try
                {
                    ICredentials credentials = request.Credentials;

                    SetKeepAliveHeaders(request, previousResponse);

                    if (usingSTSAuth)
                    {
                        // Add request headers if the server requires STS based auth.
                        STSAuthHelper.PrepareSTSRequest(request);
                    }

                    // Prepare the request, we do something like write to the request stream
                    // which needs to happen last before the request goes out
                    prepareRequest(request);

                    // Wrap the credentials in a CredentialCache in case there is a redirect
                    // and credentials need to be kept around.
                    request.Credentials = request.Credentials.AsCredentialCache(request.RequestUri);

                    WebResponse response = request.GetResponse();

                    // Cache the proxy and credentials
                    proxyCache.Add(request.Proxy);

                    credentialCache.Add(request.RequestUri, credentials);
                    credentialCache.Add(response.ResponseUri, credentials);

                    return response;
                }
                catch (WebException ex)
                {
                    using (IHttpWebResponse response = GetResponse(ex.Response))
                    {
                        if (response == null &&
                            ex.Status != WebExceptionStatus.SecureChannelFailure)
                        {
                            // No response, something went wrong so just rethrow
                            throw;
                        }

                        // Special case https connections that might require authentication
                        if (ex.Status == WebExceptionStatus.SecureChannelFailure)
                        {
                            if (continueIfFailed)
                            {
                                // Act like we got a 401 so that we prompt for credentials on the next request
                                previousStatusCode = HttpStatusCode.Unauthorized;
                                continue;
                            }
                            throw;
                        }

                        // If we were trying to authenticate the proxy or the request and succeeded, cache the result.
                        if (previousStatusCode == HttpStatusCode.ProxyAuthenticationRequired &&
                            response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired)
                        {

                            proxyCache.Add(request.Proxy);
                        }
                        else if (previousStatusCode == HttpStatusCode.Unauthorized &&
                                 response.StatusCode != HttpStatusCode.Unauthorized)
                        {
                            credentialCache.Add(request.RequestUri, request.Credentials);
                            credentialCache.Add(response.ResponseUri, request.Credentials);
                        }
                        usingSTSAuth = STSAuthHelper.TryRetrieveSTSToken(request.RequestUri, response);
                        
                        if (!IsAuthenticationResponse(response) || !continueIfFailed)
                        {
                            throw;
                        }

                        previousResponse = response;
                        previousStatusCode = previousResponse.StatusCode;
                    }
                }
            }
        }

        private static IHttpWebResponse GetResponse(WebResponse response)
        {
            var httpWebResponse = response as IHttpWebResponse;
            if (httpWebResponse == null)
            {
                var webResponse = response as HttpWebResponse;
                if (webResponse == null)
                {
                    return null;
                }
                return new HttpWebResponseWrapper(webResponse);
            }

            return httpWebResponse;
        }

        private static bool IsAuthenticationResponse(IHttpWebResponse response)
        {
            return response.StatusCode == HttpStatusCode.Unauthorized ||
                   response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
        }

        private static void SetKeepAliveHeaders(HttpWebRequest request, IHttpWebResponse previousResponse)
        {
            // KeepAlive is required for NTLM and Kerberos authentication. If we've never been authenticated or are using a different auth, we 
            // should not require KeepAlive.
            // REVIEW: The WWW-Authenticate header is tricky to parse so a Equals might not be correct. 
            if (previousResponse == null ||
                (!String.Equals(previousResponse.AuthenticationType, "NTLM", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(previousResponse.AuthenticationType, "Kerberos", StringComparison.OrdinalIgnoreCase)))
            {
                // This is to work around the "The underlying connection was closed: An unexpected error occurred on a receive."
                // exception.
                request.KeepAlive = false;
                request.ProtocolVersion = HttpVersion.Version10;
            }
        }

        private class HttpWebResponseWrapper : IHttpWebResponse
        {
            private readonly HttpWebResponse _response;
            public HttpWebResponseWrapper(HttpWebResponse response)
            {
                _response = response;
            }

            public string AuthenticationType
            {
                get
                {
                    return _response.Headers[HttpResponseHeader.WwwAuthenticate];
                }
            }

            public HttpStatusCode StatusCode
            {
                get
                {
                    return _response.StatusCode;
                }
            }

            public Uri ResponseUri
            {
                get
                {
                    return _response.ResponseUri;
                }
            }

            public NameValueCollection Headers
            {
                get
                {
                    return _response.Headers;
                }
            }

            public void Dispose()
            {
                if (_response != null)
                {
                    _response.Close();
                }
            }
        }
    }
}
