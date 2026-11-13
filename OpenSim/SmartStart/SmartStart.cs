using log4net;
using System;
using System.Net;
using System.Reflection;
using OpenMetaverse;

namespace OpenSim.Region.Framework
{
    internal class SmartStart
    {
        //SmartStart
        private static bool m_ALT_Enabled = false;

        private static Int32 m_DiagnosticsPort;
        private static string m_PrivURL;
        private static string m_MachineID;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public UUID GetALTRegion(UUID regionID, UUID agentID)
        {
            // !!!  GridGate Smart Start sends requested Region UUID to GridGate.
            // If region is on line, returns same UUID. If Offline, returns UUID for Welcome, brings up the region and teleports you to it.

            if (m_ALT_Enabled)
            {
                string url = m_PrivURL + ":" + m_DiagnosticsPort + "?alt=" + regionID + "&agent=&agentid=" + agentID + "&password=" + m_MachineID;
                m_log.DebugFormat("[AUTOLOADTELEPORT]: {0}", url);

                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);

                webRequest.Timeout = 30000; //30 Second Timeout
                m_log.DebugFormat("[SMARTSTART]: Sending request to {0}", url);

                try
                {
                    HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();
                    System.IO.StreamReader reader = new System.IO.StreamReader(webResponse.GetResponseStream());
                    string Result = String.Empty;
                    string tempStr = reader.ReadLine();
                    while (tempStr != null)
                    {
                        Result = Result + tempStr;
                        tempStr = reader.ReadLine();
                    }
                    m_log.Debug("[SMARTSTART]: Destination is " + Result);
                    regionID = OpenMetaverse.UUID.Parse(Result);
                }
                catch (WebException ex)
                {
                    m_log.Warn("[SMARTSTART]: " + ex.Message);
                }
            }
            return regionID;
        }

        public string GetALTRegion(String regionName, UUID agentID)
        {
            // !!!  GridGate Smart Start sends requested Region UUID to GridGate.
            // If region is on line, returns same UUID. If Offline, returns UUID for Welcome, brings up the region and teleports you to it.

            if (m_ALT_Enabled)
            {
                string url = m_PrivURL + ":" + m_DiagnosticsPort + "?alt=" + regionName + "&agent=&agentid=" + agentID + "&password=" + m_MachineID;
                m_log.DebugFormat("[AUTOLOADTELEPORT]: {0}", url);

                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);

                webRequest.Timeout = 30000; //30 Second Timeout
                m_log.DebugFormat("[SMARTSTART]: Sending request to {0}", url);

                try
                {
                    HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();
                    System.IO.StreamReader reader = new System.IO.StreamReader(webResponse.GetResponseStream());
                    string Result = String.Empty;
                    string tempStr = reader.ReadLine();
                    while (tempStr != null)
                    {
                        Result = Result + tempStr;
                        tempStr = reader.ReadLine();
                    }
                    m_log.Debug("[SMARTSTART]: Destination is " + Result);
                    regionName = Result;
                }
                catch (WebException ex)
                {
                    m_log.Warn("[SMARTSTART]: " + ex.Message);
                }
            }
            return regionName;
        }
    }
}
