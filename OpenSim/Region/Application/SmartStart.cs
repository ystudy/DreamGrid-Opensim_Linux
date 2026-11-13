using Nini.Config;
using OpenMetaverse;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;

namespace OpenSim
{
    public partial class SmartStart
    {
        private string m_SmartStartUrl = "http://localhost:8001";

        private System.Guid regionID;
        private Scene scene;

        public void SmartStartNotify(Scene scene, System.Guid regionID, string msg, string uid)
        {
            this.scene = scene;
            this.regionID = regionID;

            var line = File.ReadAllLines("port.txt");
            foreach (var l in line)
            {
                m_SmartStartUrl = l;
            }

            IConfig SmartStartConfig = scene.Config.Configs["SmartStart"];
            // Done GridGate Smart Start sends Region UUID to GridGate
            m_SmartStartUrl = SmartStartConfig.GetString("URL", m_SmartStartUrl);

            string url = $"{m_SmartStartUrl}?type={msg}&UUID={uid}";

            System.Net.HttpWebRequest webRequest;
            try
            {
                webRequest = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            }
            catch
            {
                return;
            }

            webRequest.Timeout = 5000; //5 Second Timeout
            webRequest.AllowWriteStreamBuffering = false;

            try
            {
                string tempStr;
                using (System.Net.HttpWebResponse webResponse = (System.Net.HttpWebResponse)webRequest.GetResponse())
                {
                    using (StreamReader reader = new StreamReader(webResponse.GetResponseStream()))
                        tempStr = reader.ReadToEnd();
                }

                if (string.IsNullOrEmpty(tempStr))
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
