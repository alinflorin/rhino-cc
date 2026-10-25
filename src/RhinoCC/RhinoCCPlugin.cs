using Rhino.PlugIns;
using Rhino.UI;

namespace RhinoCC
{
    public class RhinoCCPlugin : PlugIn
    {
        public static RhinoCCPlugin Instance { get; private set; }

        public RhinoCCPlugin()
        {
            Instance = this;
        }

        // loaded when Rhino starts, so the chat panel is there without running a command first
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
#if DEBUG
            RhinoCC.Core.Log.Sink = m => Rhino.RhinoApp.InvokeOnUiThread(new System.Action(() => Rhino.RhinoApp.WriteLine(m)));
#endif
            // the icon is an embedded .ico: a GDI icon drawn at run time did not show in the sidebar
            using (var s = typeof(RhinoCCPlugin).Assembly.GetManifestResourceStream("panel.ico"))
                Panels.RegisterPanel(this, typeof(Chat.ChatPanel), "RhinoCC Agent", new System.Drawing.Icon(s, 24, 24));
            Chat.UpdateService.Start();
            Rhino.RhinoApp.Idle += OpenPanelOnce;
            return LoadReturnCode.Success;
        }

        // on the first idle, when Rhino's window and panels exist
        private static void OpenPanelOnce(object sender, System.EventArgs e)
        {
            Rhino.RhinoApp.Idle -= OpenPanelOnce;
            try
            {
                if (Chat.AppSettings.OpenAtStartup) Panels.OpenPanel(typeof(Chat.ChatPanel).GUID);
            }
            catch (System.Exception ex)
            {
                Rhino.RhinoApp.WriteLine("RhinoCC: could not open the chat panel: " + ex.Message);
            }
        }
    }
}
