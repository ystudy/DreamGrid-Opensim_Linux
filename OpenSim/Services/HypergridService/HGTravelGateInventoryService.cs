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
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.InventoryService;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// Sits in front of the public /xinventory inventory service. That endpoint has no caller identity,
    /// so a user's inventory stays shut unless the user is on a foreign grid, which is the only time
    /// a foreign simulator has any business with it. The caller's PRINCIPAL and Owner are not trusted
    /// either: every object is looked up to find who really owns it.
    /// This narrows the exposure but does not prove who the caller is. That needs the travel token
    /// sent with every request, which foreign simulators do not do.
    /// </summary>
    public class HGTravelGateInventoryService : UserAgentServiceBase, IInventoryService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const double DECISION_SECONDS = 15;

        private const string NO_TRAVEL = "no-foreign-travel-record";
        private const string DISABLED = "operation-disabled-on-public-HG";
        private const string MISMATCH = "item-owner-mismatch";

        // Static because UserAgentService reports logouts here, and it holds no reference to this service
        private static readonly ExpiringCache<UUID, bool> m_LogoutGrace = new ExpiringCache<UUID, bool>();
        private static string m_HomeGrid = string.Empty;
        private static double m_GraceSeconds = 120;

        private readonly ExpiringCache<UUID, bool> m_Decisions = new ExpiringCache<UUID, bool>();
        private readonly IInventoryService m_Inner;
        private readonly XInventoryService m_Owners;    // sees everything, whatever the inner service hides
        private readonly IPresenceService m_PresenceService;

        public HGTravelGateInventoryService(IConfigSource config, string configName)
            : base(config)
        {
            IConfig gateConfig = config.Configs[configName];
            if (gateConfig == null)
                throw new Exception(String.Format("No section '{0}' in config file", configName));

            string inner = gateConfig.GetString("InnerServiceModule", string.Empty);
            if (inner.Length == 0)
                throw new Exception("Please specify InnerServiceModule in HGInventoryService configuration");

            m_Inner = ServerUtils.LoadPlugin<IInventoryService>(inner, new Object[] { config, configName });
            if (m_Inner == null)
                throw new Exception(String.Format("Unable to create inner inventory service from {0}", inner));

            m_Owners = new XInventoryService(config, configName);

            string presence = config.Configs["UserAgentService"]?.GetString("PresenceService", string.Empty) ?? string.Empty;
            if (presence.Length == 0)
                throw new Exception("Please specify PresenceService in UserAgentService configuration");

            m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presence, new Object[] { config });
            if (m_PresenceService == null)
                throw new Exception(String.Format("Unable to create PresenceService from {0}", presence));

            string home = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI",
                new string[] { "Startup", "Hypergrid", "UserAgentService" }, String.Empty);
            if (string.IsNullOrEmpty(home))
                throw new Exception("Please specify GatekeeperURI in the Hypergrid configuration");

            m_HomeGrid = NormalizeGrid(home);
            m_GraceSeconds = gateConfig.GetDouble("GraceSeconds", m_GraceSeconds);

            m_log.InfoFormat("[HG INVENTORY GATE]: Starting in front of {0}", inner);
        }

        /// <summary>
        /// The simulator saves changed attachments as the avatar logs out, which can land after the
        /// travel record is gone. Call this before deleting the record.
        /// </summary>
        public static void NoteLogout(HGTravelingData trip)
        {
            if (trip != null && IsForeignTrip(trip))
                m_LogoutGrace.AddOrUpdate(trip.UserID, true, m_GraceSeconds);
        }

        #region Gate

        private static string NormalizeGrid(string grid)
        {
            return grid.Trim().TrimEnd('/').ToLowerInvariant();
        }

        private static bool IsForeignTrip(HGTravelingData trip)
        {
            return trip.Data != null
                && trip.Data.TryGetValue("GridExternalName", out string grid)
                && !string.IsNullOrEmpty(grid)
                && NormalizeGrid(grid) != m_HomeGrid;
        }

        private static void Deny(string method, string reason)
        {
            m_log.InfoFormat("[HG INVENTORY GATE]: method={0}, decision=DENY, reason={1}", method, reason);
        }

        private bool IsInHomeRegion(UUID userID)
        {
            PresenceInfo[] sessions = m_PresenceService.GetAgents(new string[] { userID.ToString() });
            if (sessions != null)
            {
                foreach (PresenceInfo session in sessions)
                {
                    if (session.RegionID.IsNotZero())
                        return true;
                }
            }
            return false;
        }

        private bool CheckTravel(UUID userID)
        {
            bool atHome = IsInHomeRegion(userID);

            if (!atHome && m_LogoutGrace.TryGetValue(userID, out bool grace) && grace)
                return true;

            HGTravelingData[] trips = m_Database.GetSessions(userID);
            if (trips != null)
            {
                foreach (HGTravelingData trip in trips)
                {
                    if (!IsForeignTrip(trip))
                        continue;

                    // A trip that ended in a crash leaves its record behind, so a user at home only counts
                    // as traveling if this is the session now leaving: presence goes after they arrive there.
                    if (!atHome || m_PresenceService.GetAgent(trip.SessionID) != null)
                        return true;
                }
            }
            return false;
        }

        private bool IsOpen(string method, UUID userID)
        {
            if (m_Decisions.TryGetValue(userID, out bool open) && open)
                return true;

            // Only open is remembered, so that a refusal cannot outlast the start of a trip
            if (userID.IsNotZero() && CheckTravel(userID))
            {
                m_Decisions.AddOrUpdate(userID, true, DECISION_SECONDS);
                return true;
            }

            Deny(method, NO_TRAVEL);
            return false;
        }

        /// <summary>The user is abroad, and this folder really is theirs</summary>
        private bool MayUseFolder(string method, UUID userID, UUID folderID)
        {
            if (!IsOpen(method, userID))
                return false;

            InventoryFolderBase folder = m_Owners.GetFolder(userID, folderID);
            if (folder == null || folder.Owner != userID)
            {
                Deny(method, MISMATCH);
                return false;
            }
            return true;
        }

        /// <summary>The user is abroad, and this item really is theirs</summary>
        private bool MayUseItem(string method, UUID userID, UUID itemID)
        {
            if (!IsOpen(method, userID))
                return false;

            InventoryItemBase item = m_Owners.GetItem(userID, itemID);
            if (item == null || item.Owner != userID)
            {
                Deny(method, MISMATCH);
                return false;
            }
            return true;
        }

        #endregion

        #region Reads

        public List<InventoryFolderBase> GetInventorySkeleton(UUID userId)
        {
            return IsOpen("GETINVENTORYSKELETON", userId) ? m_Inner.GetInventorySkeleton(userId) : null;
        }

        public InventoryFolderBase GetRootFolder(UUID userID)
        {
            return IsOpen("GETROOTFOLDER", userID) ? m_Inner.GetRootFolder(userID) : null;
        }

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            return IsOpen("GETFOLDERFORTYPE", userID) ? m_Inner.GetFolderForType(userID, type) : null;
        }

        public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            return MayUseFolder("GETFOLDERCONTENT", userID, folderID) ? m_Inner.GetFolderContent(userID, folderID) : null;
        }

        public InventoryCollection[] GetMultipleFoldersContent(UUID userID, UUID[] folderIDs)
        {
            // The connector matches the answers to the request by position, and cannot take a null
            InventoryCollection[] result = new InventoryCollection[folderIDs.Length];
            for (int i = 0; i < folderIDs.Length; i++)
            {
                if (MayUseFolder("GETMULTIPLEFOLDERSCONTENT", userID, folderIDs[i]))
                    result[i] = m_Inner.GetFolderContent(userID, folderIDs[i]);

                result[i] ??= new InventoryCollection
                {
                    FolderID = folderIDs[i],
                    OwnerID = userID,
                    Folders = new List<InventoryFolderBase>(),
                    Items = new List<InventoryItemBase>()
                };
            }
            return result;
        }

        public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            return MayUseFolder("GETFOLDERITEMS", userID, folderID) ? m_Inner.GetFolderItems(userID, folderID) : null;
        }

        public InventoryItemBase GetItem(UUID userID, UUID itemID)
        {
            return MayUseItem("GETITEM", userID, itemID) ? m_Inner.GetItem(userID, itemID) : null;
        }

        public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] ids)
        {
            InventoryItemBase[] items = new InventoryItemBase[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (MayUseItem("GETMULTIPLEITEMS", userID, ids[i]))
                    items[i] = m_Inner.GetItem(userID, ids[i]);
            }
            return items;
        }

        public InventoryFolderBase GetFolder(UUID userID, UUID folderID)
        {
            return MayUseFolder("GETFOLDER", userID, folderID) ? m_Inner.GetFolder(userID, folderID) : null;
        }

        public List<InventoryItemBase> GetActiveGestures(UUID userId)
        {
            return IsOpen("GETACTIVEGESTURES", userId) ? m_Inner.GetActiveGestures(userId) : null;
        }

        public int GetAssetPermissions(UUID userID, UUID assetID)
        {
            return IsOpen("GETASSETPERMISSIONS", userID) ? m_Inner.GetAssetPermissions(userID, assetID) : 0;
        }

        public bool HasInventoryForUser(UUID userID)
        {
            return false;
        }

        #endregion

        #region Writes

        public bool AddFolder(InventoryFolderBase folder)
        {
            const string method = "ADDFOLDER";
            if (!MayUseFolder(method, folder.Owner, folder.ParentID))
                return false;

            InventoryFolderBase existing = m_Owners.GetFolder(folder.Owner, folder.ID);
            if (existing != null && existing.Owner != folder.Owner)
            {
                Deny(method, MISMATCH);
                return false;
            }
            return m_Inner.AddFolder(folder);
        }

        public bool UpdateFolder(InventoryFolderBase folder)
        {
            const string method = "UPDATEFOLDER";
            InventoryFolderBase existing = m_Owners.GetFolder(folder.Owner, folder.ID);
            if (existing == null)
                return AddFolder(folder);

            if (!MayUseFolder(method, folder.Owner, folder.ID))
                return false;

            // The base service stores the parent it is given
            if (folder.ParentID != existing.ParentID && !MayUseFolder(method, folder.Owner, folder.ParentID))
                return false;

            return m_Inner.UpdateFolder(folder);
        }

        public bool MoveFolder(InventoryFolderBase folder)
        {
            const string method = "MOVEFOLDER";
            return MayUseFolder(method, folder.Owner, folder.ID)
                && MayUseFolder(method, folder.Owner, folder.ParentID)
                && m_Inner.MoveFolder(folder);
        }

        public bool AddItem(InventoryItemBase item)
        {
            const string method = "ADDITEM";
            if (!MayUseFolder(method, item.Owner, item.Folder))
                return false;

            // The database replaces on the item ID, so adding an existing ID takes that item over.
            // An item the inner service hides must not come out of hiding this way either.
            InventoryItemBase existing = m_Owners.GetItem(item.Owner, item.ID);
            if (existing != null && (existing.Owner != item.Owner || m_Inner.GetItem(item.Owner, item.ID) == null))
            {
                Deny(method, MISMATCH);
                return false;
            }
            return m_Inner.AddItem(item);
        }

        public bool UpdateItem(InventoryItemBase item)
        {
            return MayUseItem("UPDATEITEM", item.Owner, item.ID) && m_Inner.UpdateItem(item);
        }

        public bool MoveItems(UUID ownerID, List<InventoryItemBase> items)
        {
            const string method = "MOVEITEMS";
            foreach (InventoryItemBase item in items)
            {
                if (item.Owner != ownerID)
                {
                    Deny(method, MISMATCH);
                    return false;
                }

                if (!MayUseItem(method, ownerID, item.ID) || !MayUseFolder(method, ownerID, item.Folder))
                    return false;
            }
            return m_Inner.MoveItems(ownerID, items);
        }

        public bool CreateUserInventory(UUID user)
        {
            Deny("CREATEUSERINVENTORY", DISABLED);
            return false;
        }

        public bool DeleteFolders(UUID userID, List<UUID> folderIDs)
        {
            Deny("DELETEFOLDERS", DISABLED);
            return false;
        }

        public bool PurgeFolder(InventoryFolderBase folder)
        {
            Deny("PURGEFOLDER", DISABLED);
            return false;
        }

        public bool DeleteItems(UUID userID, List<UUID> itemIDs)
        {
            Deny("DELETEITEMS", DISABLED);
            return false;
        }

        #endregion
    }
}
