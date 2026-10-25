using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace RhinoCC.Commands
{
    public class RhinoCCChatCommand : Command
    {
        public override string EnglishName => "RhinoCCChat";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(typeof(Chat.ChatPanel).GUID);
            return Result.Success;
        }
    }

    public class RhinoCCSettingsCommand : Command
    {
        public override string EnglishName => "RhinoCCSettings";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            new Chat.SettingsDialog().ShowModal(RhinoEtoApp.MainWindow);
            return Result.Success;
        }
    }
}
