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
using System.IO;
using System.Net.Http;
using System.Reflection;
using log4net;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate Smart Start's "ALT" (Auto Load Teleport) query: asks GridGate, over HTTP, whether a requested
    /// region is actually available right now. GridGate answers with the same region UUID if the region is
    /// already running (or Smart Start is disabled) or a different UUID (e.g. a Parking Lot/Welcome region) if
    /// the real target needs to boot or resume from suspension first.
    ///
    /// Shared by GatekeeperService.GetSmartStartALTRegion (hypergrid teleports), GridService.GetSmartStartALTRegion
    /// (every UUID-based region lookup), and Scene.GetSmartStartALTRegion (local teleports, the main path) - those
    /// three used to each carry their own independently-copied, independently-drifted implementation of this exact
    /// HTTP round trip. This class is the one place that logic lives now; each of the three keeps its own thin
    /// wrapper mapping this method's null-on-failure result to its own caller's historical contract (GatekeeperService
    /// and Scene's callers treat UUID.Zero as "no redirect"; GridService's caller uses the returned UUID directly as
    /// a database key with no UUID.Zero check, so its wrapper falls back to the original regionID on failure instead).
    /// </summary>
    public static class SmartStartAltClient
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Performs the Smart Start ALT HTTP round trip.
        /// </summary>
        /// <param name="regionID">Region being requested/looked up.</param>
        /// <param name="agentID">Agent making the request; UUID.Zero is valid input for callers that pass
        /// requireNonZeroAgent: false (GridService's internal lookups always do).</param>
        /// <param name="enabled">Caller's own m_SmartStartEnabled value.</param>
        /// <param name="url">Caller's own m_SmartStartUrl value.</param>
        /// <param name="machineID">Caller's own m_SmartStartMachineID value.</param>
        /// <param name="requireNonZeroAgent">True (GatekeeperService, Scene) to skip the call and return null
        /// whenever agentID is UUID.Zero; false (GridService) to always fire the query regardless of agentID.</param>
        /// <param name="logTag">Caller's own log category tag, e.g. "[GatekeeperService]", "[Scene]",
        /// "[GridService]" - every log line this method emits uses it, so log output correctly identifies its
        /// origin (the three original copies had drifted into using each other's/an unrelated class's tag).</param>
        /// <param name="clientVersion">The agent's viewer self-report string (e.g. "Firestorm-Releasex64
        /// 7.1.10.75913"), when known - GridGate's minimum-viewer-version check. Null/empty is valid and simply
        /// omits the parameter (e.g. the agent's ViewerStats CAPS report hasn't arrived yet, or this caller has no
        /// access to a per-agent cache of it).</param>
        /// <returns>Null when Smart Start is disabled, the agent guard isn't met, or the call failed for any
        /// reason (bad URL, transport error, non-success response, empty or unparsable body); otherwise the UUID
        /// GridGate responded with (which may legitimately equal regionID - "no redirect needed"). Callers map
        /// null to their own historical failure-return convention - see the class remarks.</returns>
        public static UUID? Query(UUID regionID, UUID agentID, bool enabled, string url, string machineID,
            bool requireNonZeroAgent, string logTag, string clientVersion = null)
        {
            if (!enabled) return null;
            if (requireNonZeroAgent && agentID == UUID.Zero) return null;

            Uri requestUrl;
            try
            {
                string clientVersionParam = string.IsNullOrEmpty(clientVersion)
                    ? string.Empty
                    : $"&clientversion={Uri.EscapeDataString(clientVersion)}";
                requestUrl = new Uri($"{url}?alt={regionID}&agentid={agentID}&password={machineID}{clientVersionParam}");
            }
            catch (Exception ex)
            {
                m_log.Debug($"{logTag}: SmartStart failed to create url: {ex.Message}");
                return null;
            }

            try
            {
                using HttpClient client = WebUtil.GetNewGlobalHttpClient(5000);
                using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
                using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using StreamReader reader = new(response.Content.ReadAsStream());
                string body = reader.ReadToEnd();
                if (string.IsNullOrEmpty(body)) return null;

                if (!UUID.TryParse(body, out UUID result))
                {
                    m_log.Warn($"{logTag}: SmartStart returned unparsable UUID: '{body}'");
                    return null;
                }
                return result;
            }
            catch (Exception ex)
            {
                m_log.Warn($"{logTag}: SmartStart exception: {ex.Message}");
                return null;
            }
        }
    }
}
