/* Copyright (C) 2014-2017 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.Text;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.SMB1;
using Utilities;

namespace SMBLibrary.Server.SMB1
{
    /// <summary>
    /// Session Setup helper
    /// </summary>
    public class SessionSetupHelper
    {
        internal static SMB1Command GetSessionSetupResponse(SMB1Header header, SessionSetupAndXRequest request, GSSProvider securityProvider, SMB1ConnectionState state)
        {
            SessionSetupAndXResponse response = new SessionSetupAndXResponse();
            // The PrimaryDomain field in the request is used to determine with domain controller should authenticate the user credentials,
            // However, the domain controller itself does not use this field.
            // See: http://msdn.microsoft.com/en-us/library/windows/desktop/aa378749%28v=vs.85%29.aspx
            AuthenticateMessage message = CreateAuthenticateMessage(request.AccountName, request.OEMPassword, request.UnicodePassword);
            header.Status = securityProvider.NTLMAuthenticate(state.AuthenticationContext, message);
            if (header.Status != NTStatus.STATUS_SUCCESS)
            {
                state.LogToServer(Severity.Information, "User '{0}' failed authentication, NTStatus: {1}", message.UserName, header.Status);
                return new ErrorResponse(request.CommandName);
            }

            object accessToken = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.AccessToken);
            bool? isGuest = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.IsGuest) as bool?;
            SMB1Session session;
            if (!isGuest.HasValue || !isGuest.Value)
            {
                state.LogToServer(Severity.Information, "User '{0}' authenticated successfully.", message.UserName);
                session = state.CreateSession(message.UserName, message.WorkStation, accessToken);
            }
            else
            {
                state.LogToServer(Severity.Information, "User '{0}' failed authentication, logged in as guest.", message.UserName);
                session = state.CreateSession("Guest", message.WorkStation, accessToken);
                response.Action = SessionSetupAction.SetupGuest;
            }

            if (session == null)
            {
                header.Status = NTStatus.STATUS_TOO_MANY_SESSIONS;
                return new ErrorResponse(request.CommandName);
            }

            header.UID = session.UserID;
            response.PrimaryDomain = request.PrimaryDomain;
            if ((request.Capabilities & ServerCapabilities.LargeRead) > 0)
            {
                state.LargeRead = true;
            }
            if ((request.Capabilities & ServerCapabilities.LargeWrite) > 0)
            {
                state.LargeWrite = true;
            }
            response.NativeOS = String.Empty; // "Windows Server 2003 3790 Service Pack 2"
            response.NativeLanMan = String.Empty; // "Windows Server 2003 5.2"

            return response;
        }

        internal static SMB1Command GetSessionSetupResponseExtended(SMB1Header header, SessionSetupAndXRequestExtended request, GSSProvider securityProvider, SMB1ConnectionState state)
        {
            SessionSetupAndXResponseExtended response = new SessionSetupAndXResponseExtended();

            // [MS-SMB] The Windows GSS implementation supports raw Kerberos / NTLM messages in the SecurityBlob
            byte[] outputToken;
            NTStatus status = securityProvider.AcceptSecurityContext(ref state.AuthenticationContext, request.SecurityBlob, out outputToken);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.SEC_I_CONTINUE_NEEDED)
            {
                string userName = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.UserName) as string;
                state.LogToServer(Severity.Information, "User '{0}' failed authentication, NTStatus: {1}", userName, status);
                header.Status = status;
                return new ErrorResponse(request.CommandName);
            }

            if (outputToken != null)
            {
                response.SecurityBlob = outputToken;
            }

            // According to [MS-SMB] 3.3.5.3, a UID MUST be allocated if the server returns STATUS_MORE_PROCESSING_REQUIRED
            if (header.UID == 0)
            {
                ushort? userID = state.AllocateUserID();
                if (!userID.HasValue)
                {
                    header.Status = NTStatus.STATUS_TOO_MANY_SESSIONS;
                    return new ErrorResponse(request.CommandName);
                }
                header.UID = userID.Value;
            }

            if (status == NTStatus.SEC_I_CONTINUE_NEEDED)
            {
                header.Status = NTStatus.STATUS_MORE_PROCESSING_REQUIRED;
            }
            else // header.Status == NTStatus.STATUS_SUCCESS
            {
                string userName = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.UserName) as string;
                string machineName = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.MachineName) as string;
                object accessToken = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.AccessToken);
                bool? isGuest = securityProvider.GetContextAttribute(state.AuthenticationContext, GSSAttributeName.IsGuest) as bool?;
                if (!isGuest.HasValue || !isGuest.Value)
                {
                    state.LogToServer(Severity.Information, "User '{0}' authenticated successfully.", userName);
                    state.CreateSession(header.UID, userName, machineName, accessToken);
                }
                else
                {
                    state.LogToServer(Severity.Information, "User '{0}' failed authentication, logged in as guest.", userName);
                    state.CreateSession(header.UID, "Guest", machineName, accessToken);
                    response.Action = SessionSetupAction.SetupGuest;
                }
            }
            response.NativeOS = String.Empty; // "Windows Server 2003 3790 Service Pack 2"
            response.NativeLanMan = String.Empty; // "Windows Server 2003 5.2"

            return response;
        }

        private static AuthenticateMessage CreateAuthenticateMessage(string accountNameToAuth, byte[] lmChallengeResponse, byte[] ntChallengeResponse)
        {
            AuthenticateMessage authenticateMessage = new AuthenticateMessage();
            authenticateMessage.NegotiateFlags = NegotiateFlags.UnicodeEncoding |
                                                 NegotiateFlags.OEMEncoding |
                                                 NegotiateFlags.Sign |
                                                 NegotiateFlags.LanManagerKey |
                                                 NegotiateFlags.NTLMSessionSecurity |
                                                 NegotiateFlags.AlwaysSign |
                                                 NegotiateFlags.Version |
                                                 NegotiateFlags.Use128BitEncryption |
                                                 NegotiateFlags.Use56BitEncryption;
            if (AuthenticationMessageUtils.IsNTLMv1ExtendedSecurity(lmChallengeResponse) ||
                AuthenticationMessageUtils.IsNTLMv2NTResponse(ntChallengeResponse))
            {
                authenticateMessage.NegotiateFlags |= NegotiateFlags.ExtendedSessionSecurity;
            }
            authenticateMessage.UserName = accountNameToAuth;
            authenticateMessage.LmChallengeResponse = lmChallengeResponse;
            authenticateMessage.NtChallengeResponse = ntChallengeResponse;
            authenticateMessage.Version = NTLMVersion.Server2003;
            return authenticateMessage;
        }
    }
}
