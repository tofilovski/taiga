﻿/*
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
using System.Collections.Generic;
using System.Text;
using System.Web;
using log4net;
using OpenSim.Framework.Servers.HttpServer;
using DotNetOpenAuth.Messaging;
using DotNetOpenAuth.OAuth;
using DotNetOpenAuth.OAuth.ChannelElements;
using DotNetOpenAuth.OAuth.Messages;
using DotNetOpenAuth.OpenId;
using DotNetOpenAuth.OpenId.ChannelElements;
using DotNetOpenAuth.OpenId.RelyingParty;
using OpenMetaverse;

using CapabilityIdentifier = System.Uri;
using ServiceIdentifier = System.Uri;
using OAuthServiceProvider = DotNetOpenAuth.OAuth.ServiceProvider;

namespace ModCableBeach
{
    public delegate void CreateCapabilitiesCallback(UUID sessionID, Uri identity, ref Dictionary<Uri, Uri> capabilities);

    public class Capability
    {
        public UUID CapabilityID;
        public UUID SessionID;
        public BaseStreamHandler HttpHandler;
        public bool ClientCertRequired;
        public object State;

        public Capability(UUID capabilityID, UUID sessionID, BaseStreamHandler httpHandler, bool clientCertRequired, object state)
        {
            CapabilityID = capabilityID;
            SessionID = sessionID;
            HttpHandler = httpHandler;
            ClientCertRequired = clientCertRequired;
            State = state;
        }
    }

    public class OAuthRequest
    {
        public Uri Identity;
        public UserAuthorizationRequest Request;
        public string[] CapabilityNames;

        public OAuthRequest(Uri identity, UserAuthorizationRequest request, string[] capabilityNames)
        {
            Identity = identity;
            Request = request;
            CapabilityNames = capabilityNames;
        }
    }

    public class AuthCookie
    {
        public string AuthToken;
        public Uri Identity;
        public List<string> AuthedRealms;

        public AuthCookie(string authToken, Uri identity)
        {
            AuthToken = authToken;
            Identity = identity;
            AuthedRealms = new List<string>();
        }
    }

    public static class CableBeachServerState
    {
        #region Constants

        /// <summary>Base path for capabilities generated by Cable Beach</summary>
        public const string CABLE_BEACH_CAPS_PATH = "/caps/cablebeach/";

        /// <summary>Number of minutes a capability can go unused before timing out. Default is 12 hours</summary>
        public const int CAPABILITY_TIMEOUT_MINUTES = 60 * 12;

        public const int OAUTH_OPENID_LOGIN_TIMEOUT_MINUTES = 10;

        #endregion Constants

        public static readonly ILog Log = LogManager.GetLogger("CableBeachServer");

        /// <summary>Template engine for rendering dynamic webpages</summary>
        public static readonly SmartyEngine WebTemplates = new SmartyEngine();
        /// <summary>Holds active capabilities, mapping from capability UUID to
        /// callback and session information</summary>
        public static ExpiringCache<UUID, Capability> Capabilities = new ExpiringCache<UUID, Capability>();
        /// <summary>Holds a mapping from authentication tokens (stored in
        /// cookies) to user profile data and pre-authenticated realms</summary>
        public static ExpiringCache<string, AuthCookie> AuthCookies = new ExpiringCache<string, AuthCookie>();
        public static Uri ServiceUrl;
        public static Uri OpenIDProviderUrl;
        public static OpenIdRelyingParty OpenIDRelyingParty = new OpenIdRelyingParty(new StandardRelyingPartyApplicationStore());
        public static InMemoryProviderTokenManager OAuthTokenManager = new InMemoryProviderTokenManager();
        public static OAuthServiceProvider OAuthServiceProvider;
        public static ExpiringCache<string, OAuthRequest> OAuthCurrentRequests = new ExpiringCache<string, OAuthRequest>();
        public static string ServiceRootTemplateFile;
        public static string PermissionGrantTemplateFile;

        /// <summary>Holds callbacks for each registered Cable Beach service to
        /// create capabilities on an incoming capability request</summary>
        private static Dictionary<Uri, CreateCapabilitiesCallback> m_serviceCallbacks = new Dictionary<Uri, CreateCapabilitiesCallback>();

        #region Capabilities

        public static void RegisterService(ServiceIdentifier serviceIdentifier, CreateCapabilitiesCallback capabilitiesCallback)
        {
            lock (m_serviceCallbacks)
                m_serviceCallbacks[serviceIdentifier] = capabilitiesCallback;
        }

        public static Uri CreateCapability(UUID sessionID, BaseStreamHandler httpHandler, bool clientCertRequired, object state)
        {
            UUID capID = UUID.Random();

            Capabilities.AddOrUpdate(
                capID,
                new Capability(capID, sessionID, httpHandler, clientCertRequired, state),
                TimeSpan.FromHours(CAPABILITY_TIMEOUT_MINUTES));

            return new Uri(ServiceUrl, CABLE_BEACH_CAPS_PATH + capID.ToString());
        }

        public static bool RemoveCapabilities(UUID sessionID)
        {
            // TODO: Rework how capabilities are stored so we can remove all of the caps with a given sessionID
            return false;
        }

        /// <summary>
        /// Called when an incoming request_capabilities message is received. Loops through all of the
        /// registered Cable Beach services and gives them a chance to create capabilities for each
        /// request in the dictionary
        /// </summary>
        /// <param name="requestUrl"></param>
        /// <param name="identity"></param>
        /// <param name="capabilities"></param>
        public static void CreateCapabilities(Uri identity, ref Dictionary<CapabilityIdentifier, Uri> capabilities)
        {
            UUID sessionID = UUID.Random();

            lock (m_serviceCallbacks)
            {
                foreach (KeyValuePair<ServiceIdentifier, CreateCapabilitiesCallback> entry in m_serviceCallbacks)
                {
                    try { entry.Value(sessionID, identity, ref capabilities); }
                    catch (Exception ex)
                    {
                        Log.Error("[CABLE BEACH SERVER]: Service " + entry.Key +
                            " threw an exception during capability creation: " + ex.Message, ex);
                    }
                }
            }
        }

        #endregion Capabilities

        #region Cookies

        public static bool SetAuthCookie(OSHttpRequest httpRequest, OSHttpResponse httpResponse, Uri identity, string consumer)
        {
            bool permissionGranted = false;
            HttpCookie cookie = (httpRequest.Cookies != null) ? httpRequest.Cookies["cb_auth"] : null;
            AuthCookie authCookie;
            string cookieKey;

            // Check for an existing cookie pointing to valid server-side cached info
            if (cookie != null && AuthCookies.TryGetValue(cookie.Value, out authCookie))
            {
                cookieKey = cookie.Value;

                // TODO: Linear search could be eliminated with a HashSet<>
                if (authCookie.AuthedRealms.Contains(consumer))
                    permissionGranted = true;
            }
            else
            {
                // Create a new cookie
                cookieKey = UUID.Random().ToString();
                authCookie = new AuthCookie(cookieKey, identity);
            }

            // Cookie will expire in five days
            DateTime cookieExpiration = DateTime.Now + TimeSpan.FromDays(5.0);

            // Set cookie information on the server side and in the client response
            AuthCookies.AddOrUpdate(cookieKey, authCookie, cookieExpiration);

            HttpCookie responseCookie = new HttpCookie("cb_auth", cookieKey);
            responseCookie.Expires = cookieExpiration;
            httpResponse.SetCookie(responseCookie);

            return permissionGranted;
        }

        public static void StorePermissionGrant(OSHttpRequest httpRequest, string consumer)
        {
            HttpCookie cookie = (httpRequest.Cookies != null) ? httpRequest.Cookies["cb_auth"] : null;
            AuthCookie authCookie;

            // Check for an existing cookie pointing to valid server-side cached info
            if (cookie != null && AuthCookies.TryGetValue(cookie.Value, out authCookie))
            {
                if (!authCookie.AuthedRealms.Contains(consumer))
                    authCookie.AuthedRealms.Add(consumer);
            }
        }

        #endregion Cookies

        #region OAuth

        public static byte[] MakeCheckPermissionsResponse(OSHttpRequest httpRequest, OSHttpResponse httpResponse, OAuthRequest oauthRequest)
        {
            // Return an auth cookie to the client and check if the current consumer has already been granted permissions
            bool permissionGranted = SetAuthCookie(httpRequest, httpResponse, oauthRequest.Identity, oauthRequest.Request.Callback.Authority);

            if (permissionGranted)
            {
                UserAuthorizationResponse oauthResponse = MakeOAuthSuccessResponse(oauthRequest.Request.RequestToken, oauthRequest);
                Log.Info("[CABLE BEACH SERVER]: OAuth confirmation was cached, redirecting to " + oauthRequest.Request.Callback);
                return OpenAuthHelper.MakeOpenAuthResponse(httpResponse, OAuthServiceProvider.Channel.PrepareResponse(oauthResponse));
            }
            else
            {
                // Ask the user if they want to grant capabilities to the requesting world
                return BuildPermissionGrantTemplate(oauthRequest);
            }
        }

        public static UserAuthorizationResponse MakeOAuthSuccessResponse(string requestToken, OAuthRequest oauthRequest)
        {
            // Mark the request token as authorized
            CableBeachServerState.OAuthTokenManager.AuthorizeRequestToken(requestToken);

            // Create an authorization response (including a verification code)
            UserAuthorizationResponse oauthResponse = CableBeachServerState.OAuthServiceProvider.PrepareAuthorizationResponse(oauthRequest.Request);

            // Update the verification code for this request to the newly created verification code
            try { CableBeachServerState.OAuthTokenManager.GetRequestToken(requestToken).VerificationCode = oauthResponse.VerificationCode; }
            catch (KeyNotFoundException)
            {
                CableBeachServerState.Log.Warn("[CABLE BEACH SERVER]: Did not recognize request token \"" + requestToken +
                    "\", failed to update verification code");
            }

            return oauthResponse;
        }

        #endregion OAuth

        #region HTML Templates

        public static byte[] BuildServiceRootPageTemplate(string xrdUrl)
        {
            string output = null;
            Dictionary<string, object> variables = new Dictionary<string, object>();
            variables["xrd_url"] = xrdUrl;

            try { output = WebTemplates.Render(ServiceRootTemplateFile, variables); }
            catch (Exception) { }
            if (output == null)
            {
                Log.Error("[CABLE BEACH SERVER]: Failed to render template " + ServiceRootTemplateFile);
                output = "Failed to render template " + ServiceRootTemplateFile;
            }

            return Encoding.UTF8.GetBytes(output);
        }

        public static byte[] BuildPermissionGrantTemplate(OAuthRequest oauthRequest)
        {
            string output = null;
            Dictionary<string, object> variables = new Dictionary<string, object>();
            variables["identity"] = oauthRequest.Identity;
            variables["callback"] = oauthRequest.Request.Callback;
            variables["request_token"] = oauthRequest.Request.RequestToken;
            variables["consumer"] = oauthRequest.Request.Callback.Authority;
            variables["capabilities"] = oauthRequest.CapabilityNames;

            try { output = WebTemplates.Render(PermissionGrantTemplateFile, variables); }
            catch (Exception) { }
            if (output == null)
            {
                Log.Error("[CABLE BEACH SERVER]: Failed to render template " + PermissionGrantTemplateFile);
                output = "Failed to render template " + PermissionGrantTemplateFile;
            }

            return Encoding.UTF8.GetBytes(output);
        }

        #endregion HTML Templates
    }
}
