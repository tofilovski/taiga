/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using System.Xml;
using DotNetOpenAuth.OAuth.Messages;
using DotNetOpenAuth.OpenId;
using DotNetOpenAuth.OpenId.Extensions.AttributeExchange;
using DotNetOpenAuth.OpenId.Extensions.SimpleRegistration;
using DotNetOpenAuth.OpenId.Provider;
using DotNetOpenAuth.OpenId.RelyingParty;
using log4net;
using Nwc.XmlRpc;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Services;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Statistics;
using OpenMetaverse;
using CableBeachMessages;

using OAuthConsumer = DotNetOpenAuth.OAuth.WebConsumer;
using ServiceIdentifier = System.Uri;

namespace OpenSim.Grid.UserServer.Modules
{
    public class OpenIdUserPageStreamHandler : IStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string ContentType { get { return null; } }
        public string HttpMethod { get { return m_httpMethod; } }
        public string Path { get { return m_path; } }

        string m_httpMethod;
        string m_path;

        /// <summary>
        /// Constructor
        /// </summary>
        public OpenIdUserPageStreamHandler(string httpMethod, string path, UserLoginService loginService)
        {
            m_httpMethod = httpMethod;
            m_path = path;
        }

        /// <summary>
        /// Handles all GET and POST requests for OpenID identifier pages and endpoint
        /// server communication
        /// </summary>
        public void Handle(string path, Stream request, Stream response, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            // Try and lookup this avatar
            UserProfileData profile;
            if (CableBeachState.TryGetProfile(httpRequest.Url, out profile))
            {
                if (httpRequest.Url.AbsolutePath.EndsWith(";xrd"))
                {
                    m_log.Debug("[CABLE BEACH IDP]: Returning XRD document for " + profile.Name);

                    Uri identity = new Uri(httpRequest.Url.ToString().Replace(";xrd", String.Empty));

                    // Create an XRD document from the identity URL and filesystem (inventory) service
                    XrdDocument xrd = new XrdDocument(identity.ToString());
                    xrd.Links.Add(new XrdLink(new Uri("http://specs.openid.net/auth"), null, new XrdUri(identity)));
                    xrd.Links.Add(new XrdLink(new Uri(CableBeachServices.FILESYSTEM), "application/json", new XrdUri(CableBeachState.LoginService.m_config.InventoryUrl)));

                    byte[] data = System.Text.Encoding.UTF8.GetBytes(XrdParser.WriteXrd(xrd));
                    httpResponse.ContentLength = data.Length;
                    httpResponse.ContentType = "application/xrd+xml";
                    httpResponse.OutputStream.Write(data, 0, data.Length);
                }
                else
                {
                    m_log.Debug("[CABLE BEACH IDP]: Returning user identity page for " + profile.Name);
                    Uri openidServerUrl = new Uri(httpRequest.Url, "/openid/server");
                    Uri xrdUrl = new Uri(httpRequest.Url, "/users/" + profile.FirstName + "." + profile.SurName + ";xrd");
                    CableBeachState.SendProviderUserTemplate(httpResponse, profile, openidServerUrl, xrdUrl);
                }
            }
            else
            {
                m_log.Warn("[CABLE BEACH IDP]: Couldn't find an account for identity page " + httpRequest.Url);
                // Couldn't parse an avatar name, or couldn't find the avatar in the user server
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                OpenAuthHelper.AddToBody(httpResponse, "OpenID identity not found");
            }
        }
    }

    public class OpenIdProviderStreamHandler : IStreamHandler
    {
        /// <summary>Page shown if the OpenID endpoint is requested directly</summary>
        const string ENDPOINT_PAGE =
@"<html><head><title>OpenID Endpoint</title></head><body>
This is an OpenID server endpoint, not a human-readable resource.
For more information, see <a href='http://openid.net/'>http://openid.net/</a>.
</body></html>";

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string ContentType { get { return null; } }
        public string HttpMethod { get { return m_httpMethod; } }
        public string Path { get { return m_path; } }

        string m_httpMethod;
        string m_path;

        /// <summary>
        /// Constructor
        /// </summary>
        public OpenIdProviderStreamHandler(string httpMethod, string path, UserLoginService loginService)
        {
            m_httpMethod = httpMethod;
            m_path = path;
        }

        /// <summary>
        /// Handles all GET and POST requests for OpenID provider communication
        /// </summary>
        public void Handle(string path, Stream request, Stream response, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            try
            {
                if (httpRequest.HasEntityBody)
                    OpenIDServerPostHandler(httpRequest, httpResponse);
                else
                    OpenIDServerGetHandler(httpRequest, httpResponse);
            }
            catch (Exception ex)
            {
                m_log.Error("[CABLE BEACH IDP]: HTTP request handling failed: " + ex.Message, ex);
                httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
                OpenAuthHelper.AddToBody(httpResponse, ex.Message);
            }
        }

        void OpenIDServerGetHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            IRequest openidRequest = CableBeachState.Provider.GetRequest(OpenAuthHelper.GetRequestInfo(httpRequest));

            if (openidRequest != null)
            {
                if (openidRequest is DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest)
                {
                    DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest authRequest = (DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest)openidRequest;

                    // Check for cancellations
                    if (!String.IsNullOrEmpty(httpRequest.QueryString["cancel"]))
                    {
                        m_log.Warn("[CABLE BEACH IDP]: Request to " + httpRequest.Url + " contained a cancel argument, sending a negative assertion");
                        authRequest.IsAuthenticated = false;
                        OpenAuthHelper.OpenAuthResponseToHttp(httpResponse, CableBeachState.Provider.PrepareResponse(openidRequest));
                        return;
                    }

                    // Check for a login cookie
                    HttpCookie cookie = (httpRequest.Cookies != null) ? httpRequest.Cookies["cb_openid_auth"] : null;
                    AuthCookie authCookie;
                    if (cookie != null && CableBeachState.AuthCookies.TryGetValue(cookie.Value, out authCookie))
                    {
                        // Login was cached
                        ClaimsRequest claimsRequest = openidRequest.GetExtension<ClaimsRequest>();
                        DoCachedAuthentication(authRequest, claimsRequest, authCookie);
                    }
                    else if (authRequest.IsDirectedIdentity)
                    {
                        // Directed identity, send the generic login form
                        m_log.Debug("[CABLE BEACH IDP]: (GET) Sending directed provider login form");
                        CableBeachState.SendProviderDirectedLoginTemplate(httpResponse, authRequest.Realm.ToString(), httpRequest, null);
                    }
                    else
                    {
                        // Identity already selected, try to pull up the profile
                        UserProfileData profile;
                        if (CableBeachState.TryGetProfile((UriIdentifier)authRequest.ClaimedIdentifier, out profile))
                        {
                            m_log.Debug("[CABLE BEACH IDP]: (GET) Sending provider login form for " + profile.Name);
                            CableBeachState.SendProviderLoginTemplate(httpResponse, profile.FirstName, profile.SurName, profile.ID, authRequest.Realm.ToString(),
                                httpRequest, null);
                        }
                        else
                        {
                            m_log.Error("[CABLE BEACH IDP]: (GET) Attempted a non-directed login with an unknown identifier " + authRequest.ClaimedIdentifier);
                            authRequest.IsAuthenticated = false;
                        }
                    }
                }

                if (openidRequest.IsResponseReady)
                    OpenAuthHelper.OpenAuthResponseToHttp(httpResponse, CableBeachState.Provider.PrepareResponse(openidRequest));
            }
            else
            {
                // Standard HTTP GET was made on the OpenID endpoint, send the client the default error page
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                OpenAuthHelper.AddToBody(httpResponse, ENDPOINT_PAGE);
            }
        }

        void OpenIDServerPostHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            IRequest openidRequest = CableBeachState.Provider.GetRequest(OpenAuthHelper.GetRequestInfo(httpRequest));

            if (openidRequest != null)
            {
                if (openidRequest is DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest)
                {
                    DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest authRequest = (DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest)openidRequest;
                    ClaimsRequest claimsRequest = openidRequest.GetExtension<ClaimsRequest>();
                    byte[] postBody = httpRequest.GetBody();

                    if (authRequest.IsDirectedIdentity)
                    {
                        NameValueCollection postData = null;
                        string first = null, last = null, pass = null;

                        // Get the firstname, lastname, and password from the POST data
                        if (postBody.Length > 0)
                        {
                            postData = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(postBody, 0, postBody.Length), Encoding.UTF8);

                            if (postData != null)
                            {
                                first = postData["first"];
                                last = postData["last"];
                                pass = postData["pass"];
                            }
                        }

                        if (!DoAuthentication(httpResponse, authRequest, claimsRequest, first, last, pass))
                        {
                            m_log.Debug("[CABLE BEACH IDP]: (GET) Sending directed provider login form");
                            CableBeachState.SendProviderDirectedLoginTemplate(httpResponse, authRequest.Realm.ToString(), httpRequest, postData);
                        }
                    }
                    else
                    {
                        // Identity already selected
                        Uri claimedIdentity = (UriIdentifier)authRequest.ClaimedIdentifier;

                        // Try and lookup this avatar
                        UserProfileData profile;
                        if (CableBeachState.TryGetProfile(claimedIdentity, out profile))
                        {
                            NameValueCollection postData = null;
                            string pass = null;

                            // Get the password from the POST data
                            if (postBody.Length > 0)
                            {
                                postData = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(postBody, 0, postBody.Length), Encoding.UTF8);
                                pass = (postData != null) ? postData["pass"] : null;
                            }

                            if (!DoAuthentication(httpResponse, authRequest, claimsRequest, profile, pass))
                            {
                                m_log.Debug("[CABLE BEACH IDP]: (POST) Sending provider login form for " + profile.Name);
                                CableBeachState.SendProviderLoginTemplate(httpResponse, profile.FirstName, profile.SurName, profile.ID, authRequest.Realm.ToString(),
                                    httpRequest, postData);
                            }
                        }
                        else
                        {
                            m_log.Error("[CABLE BEACH IDP]: (POST) Attempted a non-directed login with an unknown identifier " + authRequest.ClaimedIdentifier);
                        }
                    }
                }

                if (openidRequest.IsResponseReady)
                    OpenAuthHelper.OpenAuthResponseToHttp(httpResponse, CableBeachState.Provider.PrepareResponse(openidRequest));
            }
            else
            {
                m_log.Warn("[CABLE BEACH IDP]: Got a POST to a URL with missing or invalid OpenID data: " + httpRequest.Url);
                OpenAuthHelper.AddToBody(httpResponse, ENDPOINT_PAGE);
            }
        }

        #region Helper Methods

        void DoCachedAuthentication(DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest authRequest, ClaimsRequest claimsRequest, AuthCookie authCookie)
        {
            m_log.Info("[CABLE BEACH IDP]: Using cached login credentials for " + authCookie.UserProfile.Name);

            authRequest.ClaimedIdentifier = authCookie.Identity;
            authRequest.IsAuthenticated = true;

            if (claimsRequest != null)
            {
                // Fill in a few Simple Registration values if there was a request for SREG data
                ClaimsResponse claimsResponse = claimsRequest.CreateResponse();
                claimsResponse.Email = authCookie.UserProfile.Email;
                claimsResponse.FullName = authCookie.UserProfile.Name;
                claimsResponse.BirthDate = Utils.UnixTimeToDateTime(authCookie.UserProfile.Created);
                authRequest.AddResponseExtension(claimsResponse);

                m_log.Debug("[CABLE BEACH IDP]: Appended SREG values to the positive assertion response");
            }
        }

        bool DoAuthentication(OSHttpResponse httpResponse, DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest authRequest, ClaimsRequest claimsRequest,
            UserProfileData profile, string pass)
        {
            bool authSuccess = false;

            if (!String.IsNullOrEmpty(pass))
            {
                authSuccess = CableBeachState.LoginService.AuthenticateUser(profile, pass);
                m_log.Info("[CABLE BEACH IDP]: Password match result for " + profile.Name + ": " + authSuccess);

                if (authSuccess)
                {
                    // Mark the OpenID request as successfully authenticated
                    authRequest.IsAuthenticated = true;

                    // Cache this login
                    SetCookie(httpResponse, new Uri(authRequest.ClaimedIdentifier), profile);

                    if (claimsRequest != null)
                    {
                        // Fill in a few Simple Registration values if there was a request for SREG data
                        ClaimsResponse claimsResponse = claimsRequest.CreateResponse();
                        claimsResponse.Email = profile.Email;
                        claimsResponse.FullName = profile.Name;
                        claimsResponse.BirthDate = Utils.UnixTimeToDateTime(profile.Created);
                        authRequest.AddResponseExtension(claimsResponse);

                        m_log.Debug("[CABLE BEACH IDP]: Appended SREG values to the positive assertion response");
                    }
                }
            }
            else
            {
                // Valid POST but missing the password field
                m_log.Warn("[CABLE BEACH IDP]: POST is missing pass field for " + profile.Name);
            }

            return authSuccess;
        }

        bool DoAuthentication(OSHttpResponse httpResponse, DotNetOpenAuth.OpenId.Provider.IAuthenticationRequest authRequest, ClaimsRequest claimsRequest,
            string first, string last, string pass)
        {
            bool authSuccess = false;

            if (!String.IsNullOrEmpty(first) && !String.IsNullOrEmpty(last) && !String.IsNullOrEmpty(pass))
            {
                UserProfileData profile;
                if (CableBeachState.TryGetProfile(first, last, out profile))
                {
                    // Set the claimed identifier to the URL of the given identity
                    Uri identity = new Uri(CableBeachState.UserServerUrl, String.Format("/users/{0}.{1}", profile.FirstName, profile.SurName));
                    authRequest.ClaimedIdentifier = identity;

                    authSuccess = CableBeachState.LoginService.AuthenticateUser(profile, pass);
                    m_log.Info("[CABLE BEACH IDP]: Password match result for " + profile.Name + ": " + authRequest.IsAuthenticated);

                    if (authSuccess)
                    {
                        // Mark the OpenID request as successfully authenticated
                        authRequest.IsAuthenticated = true;

                        // Cache this login
                        SetCookie(httpResponse, identity, profile);

                        if (claimsRequest != null)
                        {
                            // Fill in a few Simple Registration values if there was a request for SREG data
                            ClaimsResponse claimsResponse = claimsRequest.CreateResponse();
                            claimsResponse.Email = profile.Email;
                            claimsResponse.FullName = profile.Name;
                            claimsResponse.BirthDate = Utils.UnixTimeToDateTime(profile.Created);
                            authRequest.AddResponseExtension(claimsResponse);

                            m_log.Debug("[CABLE BEACH IDP]: Appended SREG values to the positive assertion response");
                        }
                    }
                }
                else
                {
                    m_log.Warn("[CABLE BEACH IDP]: Profile for user " + first + " " + last + " not found");
                }
            }
            else
            {
                // Valid POST but missing one or more fields
                m_log.Warn("[CABLE BEACH IDP]: POST is missing first, last, or pass field, sending directed login form");
            }

            return authSuccess;
        }

        void SetCookie(OSHttpResponse httpResponse, Uri identity, UserProfileData profile)
        {
            string cookieKey = UUID.Random().ToString();

            // Cookie will expire in five days
            DateTime cookieExpiration = DateTime.Now + TimeSpan.FromDays(5.0);

            // Cache the server-side data
            CableBeachState.AuthCookies.AddOrUpdate(cookieKey, new AuthCookie(cookieKey, identity, profile), cookieExpiration);

            // Create the cookie
            HttpCookie responseCookie = new HttpCookie("cb_openid_auth", cookieKey);
            responseCookie.Expires = cookieExpiration;
            httpResponse.SetCookie(responseCookie);
        }

        #endregion Helper Methods
    }

    public class OpenIdLoginStreamHandler : IStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string ContentType { get { return null; } }
        public string HttpMethod { get { return m_httpMethod; } }
        public string Path { get { return m_path; } }

        string m_httpMethod;
        string m_path;

        /// <summary>
        /// Constructor
        /// </summary>
        public OpenIdLoginStreamHandler(string httpMethod, string path, UserLoginService loginService)
        {
            m_httpMethod = httpMethod;
            m_path = path;
        }

        /// <summary>
        /// Handles all GET and POST requests for OpenID logins
        /// </summary>
        public void Handle(string path, Stream request, Stream response, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            try
            {
                UUID sessionID;
                bool isPost = httpRequest.HasEntityBody;

                if (IsXmlRpcLogin(httpRequest.Url, out sessionID))
                    XmlRpcLoginHandler(httpRequest, httpResponse);
                else if (isPost)
                    OpenIDLoginPostHandler(httpRequest, httpResponse);
                else
                    OpenIDLoginGetHandler(httpRequest, httpResponse);
            }
            catch (Exception ex)
            {
                m_log.Error("[CABLE BEACH LOGIN]: HTTP request handling failed: " + ex.Message, ex);
                httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
                OpenAuthHelper.AddToBody(httpResponse, ex.Message);
            }
        }

        #region HTTP Handlers

        void OpenIDLoginGetHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            if (httpRequest.Url.AbsolutePath.EndsWith("openid_callback"))
            {
                #region OpenID Callback

                IAuthenticationResponse authResponse = CableBeachState.RelyingParty.GetResponse(OpenAuthHelper.GetRequestInfo(httpRequest));

                if (authResponse != null)
                {
                    if (authResponse.Status == AuthenticationStatus.Authenticated)
                    {
                        // OpenID authentication succeeded
                        Uri identity = new Uri(authResponse.ClaimedIdentifier.ToString());

                        // Check if this identity is authorized for access. This check is done here for the second time
                        // because the ClaimedIdentifier after authentication has finished is not necessarily the original
                        // OpenID URL entered into the login form
                        if (CableBeachState.IsIdentityAuthorized(identity))
                        {
                            string firstName = null, lastName = null, email = null;

                            // Get the Simple Registration attributes the IDP returned, if any
                            ClaimsResponse sreg = authResponse.GetExtension<ClaimsResponse>();
                            if (sreg != null)
                            {
                                if (!String.IsNullOrEmpty(sreg.FullName))
                                {
                                    string[] firstLast = sreg.FullName.Split(' ');
                                    if (firstLast.Length == 2)
                                    {
                                        firstName = firstLast[0];
                                        lastName = firstLast[1];
                                    }
                                }

                                email = sreg.Email;
                            }

                            CableBeachState.StartLogin(httpRequest, httpResponse, identity, firstName, lastName, email, CableBeachAuthMethods.OPENID);
                        }
                        else
                        {
                            CableBeachState.SendLoginTemplate(httpResponse, null, identity + " is not authorized to access this world");
                        }
                    }
                    else
                    {
                        // Parse an error message out of authResponse
                        string errorMsg = (authResponse.Exception != null) ?
                            authResponse.Exception.Message :
                            authResponse.Status.ToString();

                        CableBeachState.SendLoginTemplate(httpResponse, null, errorMsg);
                    }
                }
                else
                {
                    httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                    OpenAuthHelper.AddToBody(httpResponse, "Invalid or missing OpenID callback data");
                }

                #endregion OpenID Callback
            }
            else if (httpRequest.Url.AbsolutePath.EndsWith("oauth_callback"))
            {
                #region OAuth Callback

                ServiceRequestsData stateData;
                string requestToken = OpenAuthHelper.GetQueryValue(httpRequest.Url.Query, "oauth_token");

                if (!String.IsNullOrEmpty(requestToken) && CableBeachState.CurrentServiceRequests.TryGetValue(requestToken, out stateData))
                {
                    ServiceIdentifier serviceIdentifier = CableBeachState.GetCurrentService(stateData.ServiceRequirements);
                    Service service;
                    CapabilityRequirements capRequirements;

                    if (serviceIdentifier != null)
                    {
                        if (stateData.Services.TryGetValue(serviceIdentifier, out service) &&
                            stateData.ServiceRequirements.TryGetValue(serviceIdentifier, out capRequirements))
                        {
                            try
                            {
                                OAuthConsumer consumer = new OAuthConsumer(OpenAuthHelper.CreateServiceProviderDescription(service), CableBeachState.OAuthTokenManager);
                                AuthorizedTokenResponse tokenResponse = consumer.ProcessUserAuthorization(OpenAuthHelper.GetRequestInfo(httpRequest));

                                // We actually don't need the access token at all since the capabilities should be in this response.
                                // Parse the capabilities out of ExtraData
                                CapabilityRequirements newCaps = new CapabilityRequirements();
                                foreach (KeyValuePair<string, string> capability in tokenResponse.ExtraData)
                                {
                                    Uri capIdentifier, capUri;
                                    if (Uri.TryCreate(capability.Key, UriKind.Absolute, out capIdentifier) &&
                                        Uri.TryCreate(capability.Value, UriKind.Absolute, out capUri))
                                    {
                                        newCaps[capIdentifier] = capUri;
                                    }
                                }

                                m_log.Info("[CABLE BEACH LOGIN]: Fetched " + newCaps.Count + " capabilities through OAuth from " + service.OAuthGetAccessToken);

                                // Update the capabilities for this service
                                stateData.ServiceRequirements[serviceIdentifier] = newCaps;
                            }
                            catch (Exception ex)
                            {
                                m_log.Error("[CABLE BEACH LOGIN]: Failed to exchange request token for capabilities at " + service.OAuthGetAccessToken + ": " + ex.Message);
                                CableBeachState.SendLoginTemplate(httpResponse, null, "OAuth request to " + service.OAuthGetAccessToken + " failed: " + ex.Message);
                                return;
                            }
                        }
                        else
                        {
                            m_log.Error("[CABLE BEACH LOGIN]: OAuth state data corrupted, could not find service or service requirements for " + serviceIdentifier);
                            CableBeachState.SendLoginTemplate(httpResponse, null, "OAuth state data corrupted, please try again");
                            return;
                        }
                    }
                    else
                    {
                        m_log.Warn("[CABLE BEACH LOGIN]: OAuth callback fired but there are no unfulfilled services. Could be a browser refresh");
                    }

                    // Check if we need to continue the cap requesting process
                    CableBeachState.GetCapabilitiesOrCompleteLogin(httpRequest, httpResponse, stateData, requestToken);
                }
                else
                {
                    // A number of different things could lead here (incomplete login sequence, browser refresh of a completed sequence).
                    // Safest thing to do would be to redirect back to the login screen
                    httpResponse.StatusCode = (int)HttpStatusCode.Found;
                    httpResponse.AddHeader("Location", new Uri(CableBeachState.UserServerUrl, "/login/").ToString());
                }

                #endregion OAuth Callback
            }
            else
            {
                // Make sure we are starting from the correct URL
                if (httpRequest.Url.Authority != CableBeachState.UserServerUrl.Authority)
                {
                    m_log.Debug("[CABLE BEACH LOGIN]: Redirecting from " + httpRequest.Url + " to " + CableBeachState.UserServerUrl);
                    httpResponse.StatusCode = (int)HttpStatusCode.Redirect;
                    httpResponse.RedirectLocation = new Uri(CableBeachState.UserServerUrl, "/login/").ToString();
                }
                else if (httpRequest.Query.ContainsKey("openid_identifier"))
                {
                    OpenIDLoginFormHandler(httpRequest, httpResponse, httpRequest.Query["openid_identifier"] as string);
                }
                else
                {
                    // TODO: Check for a client cookie with an authenticated session
                    CableBeachState.SendLoginTemplate(httpResponse, null, null);
                }
            }
        }

        void OpenIDLoginPostHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            byte[] requestData = httpRequest.GetBody();
            string queryString = HttpUtility.UrlDecode(requestData, System.Text.Encoding.UTF8);
            NameValueCollection query = System.Web.HttpUtility.ParseQueryString(queryString);
            string openidIdentifier = query["openid_identifier"];

            OpenIDLoginFormHandler(httpRequest, httpResponse, openidIdentifier);
        }

        void OpenIDLoginFormHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse, string openidIdentifier)
        {
            Uri identity;
            Identifier identifier;

            if (String.IsNullOrEmpty(openidIdentifier))
            {
                m_log.Warn("[CABLE BEACH LOGIN]: Received an OpenID login with an empty OpenID URL field");
                CableBeachState.SendLoginTemplate(httpResponse, null, "Please fill in the OpenID URL field");
                return;
            }

            if (UriIdentifier.TryParse(openidIdentifier, out identifier) && Uri.TryCreate(openidIdentifier, UriKind.Absolute, out identity))
            {
                // Check if this identity is authorized for access
                if (CableBeachState.IsIdentityAuthorized(identity))
                {
                    string baseURL = String.Format("{0}://{1}", httpRequest.Url.Scheme, httpRequest.Url.Authority);
                    Realm realm = new Realm(baseURL);

                    try
                    {
                        m_log.Info("[CABLE BEACH LOGIN]: Starting OpenID auth request for " + identity);

                        DotNetOpenAuth.OpenId.RelyingParty.IAuthenticationRequest authRequest =
                            CableBeachState.RelyingParty.CreateRequest(identifier, realm, new Uri(httpRequest.Url, "/login/openid_callback"));

                        // Add a Simple Registration request to the OpenID request
                        ClaimsRequest sreg = new ClaimsRequest();
                        sreg.BirthDate = DemandLevel.Request;
                        sreg.Email = DemandLevel.Request;
                        sreg.FullName = DemandLevel.Request;
                        sreg.Gender = DemandLevel.Request;
                        sreg.Language = DemandLevel.Request;
                        sreg.Nickname = DemandLevel.Request;
                        sreg.TimeZone = DemandLevel.Request;
                        authRequest.AddExtension(sreg);

                        // Add an Attribute Exchange request to the OpenID request
                        FetchRequest ax = new FetchRequest();
                        ax.Attributes.AddOptional(AvatarAttributes.BIOGRAPHY.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.BIRTH_DATE.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.COMPANY.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.EMAIL.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.FIRST_NAME.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.LANGUAGE.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.LAST_NAME.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.TIMEZONE.ToString());
                        ax.Attributes.AddOptional(AvatarAttributes.WEBSITE.ToString());
                        authRequest.AddExtension(ax);

                        OpenAuthHelper.OpenAuthResponseToHttp(httpResponse, authRequest.RedirectingResponse);
                    }
                    catch (Exception ex)
                    {
                        m_log.Error("[CABLE BEACH LOGIN]: OpenID login failed: " + ex.Message, ex);
                        CableBeachState.SendLoginTemplate(httpResponse, null, "OpenID login failed: " + ex.Message);
                    }
                }
                else
                {
                    m_log.Warn("[CABLE BEACH LOGIN]: Identity " + identity + " was denied access");
                    CableBeachState.SendLoginTemplate(httpResponse, null, identity + " is not authorized to access this world");
                }
            }
        }

        /// <summary>
        /// We can't bind XML-RPC handlers to specific paths with the OpenSim
        /// HTTP server, so this method works around that fact
        /// </summary>
        void XmlRpcLoginHandler(OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            XmlRpcRequest xmlRpcRequest = null;
            XmlRpcResponse xmlRpcResponse = new XmlRpcResponse();

            try
            {
                using (TextReader requestReader = new StreamReader(httpRequest.InputStream))
                    xmlRpcRequest = CableBeachState.XmlRpcLoginDeserializer.Deserialize(requestReader) as XmlRpcRequest;
            }
            catch (XmlException) { }

            if (xmlRpcRequest != null)
            {
                string methodName = xmlRpcRequest.MethodName;
                m_log.Info("[CABLE BEACH XMLRPC]: Received an incoming XML-RPC request: " + methodName);

                if (methodName != null && methodName.Equals("login_to_simulator", StringComparison.InvariantCultureIgnoreCase))
                {
                    xmlRpcRequest.Params.Add(httpRequest.RemoteIPEndPoint); // Param[1]

                    try
                    {
                        xmlRpcResponse = LoginHandler(xmlRpcRequest, httpRequest.Url);
                    }
                    catch (Exception e)
                    {
                        // Code set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                        xmlRpcResponse.SetFault(-32603, String.Format("Requested method [{0}] threw exception: {1}",
                            methodName, e));
                    }
                }
            }
            else
            {
                m_log.Warn("[CABLE BEACH XMLRPC]: Received a login request with an invalid or missing XML-RPC body");
            }

            #region Send the Response

            httpResponse.ContentType = "text/xml";
            httpResponse.SendChunked = false;
            httpResponse.ContentEncoding = System.Text.Encoding.UTF8;

            try
            {
                MemoryStream memoryStream = new MemoryStream();
                using (XmlTextWriter writer = new XmlTextWriter(memoryStream, System.Text.Encoding.UTF8))
                {
                    XmlRpcResponseSerializer.Singleton.Serialize(writer, xmlRpcResponse);
                    writer.Flush();

                    httpResponse.ContentLength = memoryStream.Length;
                    httpResponse.OutputStream.Write(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                }
            }
            catch (Exception ex)
            {
                m_log.Warn("[CABLE BEACH XMLRPC]: Error writing to the response stream: " + ex.Message);
            }

            #endregion Send the Response
        }

        XmlRpcResponse LoginHandler(XmlRpcRequest request, Uri requestUrl)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable requestData = (Hashtable)request.Params[0];
            IPEndPoint remoteClient = null;
            if (request.Params.Count > 1)
                remoteClient = request.Params[1] as IPEndPoint;

            UserProfileData userProfile;
            LoginResponse logResponse = new LoginResponse();

            UUID sessionID;
            IsXmlRpcLogin(requestUrl, out sessionID);
            m_log.Info("[CABLE BEACH XMLRPC]: XML-RPC Received login request message with sessionID " + sessionID);

            string startLocationRequest = "last";
            if (requestData.Contains("start"))
                startLocationRequest = (requestData["start"] as string) ?? "last";

            string clientVersion = "Unknown";
            if (requestData.Contains("version"))
                clientVersion = (requestData["version"] as string) ?? "Unknown";

            if (TryAuthenticateXmlRpcLogin(sessionID, out userProfile))
            {
                try
                {
                    UUID agentID = userProfile.ID;
                    LoginService.InventoryData skeleton = null;

                    try { skeleton = CableBeachState.LoginService.GetInventorySkeleton(agentID); }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[CABLE BEACH XMLRPC]: Error retrieving inventory skeleton of agent {0} - {1}",
                            agentID, e);

                        // Let's not panic
                        if (!CableBeachState.LoginService.AllowLoginWithoutInventory())
                            return logResponse.CreateLoginInventoryFailedResponse();
                    }

                    #region Inventory Skeletons

                    if (skeleton != null)
                    {
                        ArrayList AgentInventoryArray = skeleton.InventoryArray;

                        Hashtable InventoryRootHash = new Hashtable();
                        InventoryRootHash["folder_id"] = skeleton.RootFolderID.ToString();
                        ArrayList InventoryRoot = new ArrayList();
                        InventoryRoot.Add(InventoryRootHash);

                        logResponse.InventoryRoot = InventoryRoot;
                        logResponse.InventorySkeleton = AgentInventoryArray;
                    }

                    // Inventory Library Section
                    Hashtable InventoryLibRootHash = new Hashtable();
                    InventoryLibRootHash["folder_id"] = "00000112-000f-0000-0000-000100bba000";
                    ArrayList InventoryLibRoot = new ArrayList();
                    InventoryLibRoot.Add(InventoryLibRootHash);

                    logResponse.InventoryLibRoot = InventoryLibRoot;
                    logResponse.InventoryLibraryOwner = CableBeachState.LoginService.GetLibraryOwner();
                    logResponse.InventoryLibrary = CableBeachState.LoginService.GetInventoryLibrary();

                    logResponse.CircuitCode = Util.RandomClass.Next();
                    logResponse.Lastname = userProfile.SurName;
                    logResponse.Firstname = userProfile.FirstName;
                    logResponse.AgentID = agentID;
                    logResponse.SessionID = userProfile.CurrentAgent.SessionID;
                    logResponse.SecureSessionID = userProfile.CurrentAgent.SecureSessionID;
                    logResponse.Message = CableBeachState.LoginService.GetMessage();
                    logResponse.BuddList = CableBeachState.LoginService.ConvertFriendListItem(CableBeachState.LoginService.UserManager.GetUserFriendList(agentID));
                    logResponse.StartLocation = startLocationRequest;

                    #endregion Inventory Skeletons

                    if (CableBeachState.LoginService.CustomiseResponse(logResponse, userProfile, startLocationRequest, remoteClient))
                    {
                        userProfile.LastLogin = userProfile.CurrentAgent.LoginTime;
                        CableBeachState.LoginService.CommitAgent(ref userProfile);

                        // If we reach this point, then the login has successfully logged onto the grid
                        if (StatsManager.UserStats != null)
                            StatsManager.UserStats.AddSuccessfulLogin();

                        m_log.DebugFormat("[CABLE BEACH XMLRPC]: Authentication of user {0} {1} successful. Sending response to client",
                            userProfile.FirstName, userProfile.FirstName);

                        return logResponse.ToXmlRpcResponse();
                    }
                    else
                    {
                        m_log.ErrorFormat("[CABLE BEACH XMLRPC]: Informing user {0} {1} that login failed due to an unavailable region",
                            userProfile.FirstName, userProfile.FirstName);

                        return logResponse.CreateDeadRegionResponse();
                    }
                }
                catch (Exception e)
                {
                    m_log.Error("[CABLE BEACH XMLRPC]: Login failed, returning a blank response. Error: " + e);
                    return response;
                }
            }
            else
            {
                m_log.Warn("[CABLE BEACH XMLRPC]: Authentication failed using sessionID " + sessionID + ", there are " +
                    CableBeachState.PendingLogins.Count + " valid pending logins");
                return logResponse.CreateLoginFailedResponse();
            }
        }

        #endregion HTTP Handlers

        bool IsXmlRpcLogin(Uri requestUrl, out UUID sessionID)
        {
            for (int i = requestUrl.Segments.Length - 1; i >= 0; i--)
            {
                if (UUID.TryParse(requestUrl.Segments[i].Replace("/", String.Empty), out sessionID))
                    return true;
            }

            sessionID = UUID.Zero;
            return false;
        }

        bool TryAuthenticateXmlRpcLogin(UUID sessionID, out UserProfileData userProfile)
        {
            // No need to delete the sessionID from pendinglogins, it will expire eventually
            if (CableBeachState.PendingLogins.TryGetValue(sessionID, out userProfile))
                return true;

            userProfile = null;
            return false;
        }
    }
}
