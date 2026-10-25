using System.Collections.Generic;
using System.Linq;

namespace RhinoCC.Core.Agents
{
    public static class Engines
    {
        public static IReadOnlyList<IAgentEngine> All { get; } = new IAgentEngine[] { new ClaudeEngine(), new CodexEngine() };

        public static IAgentEngine Get(string id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
    }
}
