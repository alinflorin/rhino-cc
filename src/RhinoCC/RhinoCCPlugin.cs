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
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.FromArgb(40, 40, 40));
                    g.DrawString("CC", new System.Drawing.Font("Arial", 12, System.Drawing.FontStyle.Bold), System.Drawing.Brushes.White, 2, 7);
                }
                var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                Panels.RegisterPanel(this, typeof(Chat.ChatPanel), "RhinoCC Agent", icon);
            }
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
