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

using System.Collections.Concurrent;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate minimum-viewer-version check: the most recently reported ViewerStats client_version string per
    /// agent. Written by OpenSim.Region.OptionalModules.UserStatistics.WebStatsModule.ParseViewerStats, the only
    /// place a viewer's self-reported client_version is ever known (it arrives via the viewer's own periodic
    /// ViewerStats CAPS post, not at login). Read by Scene.GetSmartStartALTRegion so it can be included on the next
    /// SmartStart ALT round trip to GridGate (see SmartStartAltClient.Query's clientVersion parameter) without
    /// WebStatsModule needing its own outbound HTTP call.
    ///
    /// Lives in OpenSim.Framework - not in either WebStatsModule's OptionalModules assembly or Scene's
    /// Region.Framework assembly - because OptionalModules already depends on Region.Framework (for Scene,
    /// ScenePresence) and a reverse dependency the other way would be backwards: core Framework/Scenes must never
    /// depend on an optional add-on module. Both existing assemblies already reference OpenSim.Framework (as does
    /// SmartStartAltClient, which lives here too), so this is the one place both sides can share the cache without
    /// a circular or backwards project reference.
    ///
    /// Never cleared per-agent - a stale entry for a logged-out agent is harmless, since SmartStartAltClient.Query
    /// is only ever consulted for a currently-connecting agent's own UUID.
    /// </summary>
    public static class ClientVersionCache
    {
        public static readonly ConcurrentDictionary<UUID, string> ClientVersionByAgent = new ConcurrentDictionary<UUID, string>();
    }
}
