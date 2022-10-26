using System.ComponentModel;

namespace Gizmo.Server.Web.EventMonitor
{
    /// <summary>
    /// Real time endpoint configuration.
    /// </summary>
    public class RealTimeConfig
    {
        #region PROPERTIES

        /// <summary>
        /// Real time command URL.
        /// </summary>
        [DefaultValue("http://localhost")]
        public string RealTimeUrl { get; set; }

        #endregion
    }
}
