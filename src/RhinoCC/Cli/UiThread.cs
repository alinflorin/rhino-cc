using System;
using System.Threading;
using Rhino;
using RhinoCC.Core.Cli;

namespace RhinoCC.Cli
{
    internal static class UiThread
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        /// <summary>Runs on Rhino's UI thread and waits for the result.</summary>
        public static CliResult Run(Func<CliResult> f)
        {
            CliResult result = null;
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    try { result = f(); }
                    catch (Exception e) { error = e; }
                    finally { done.Set(); }
                }));
                if (!done.Wait(Timeout)) return CliResult.Fail("error: Rhino did not answer in 2 minutes (is a dialog or a command waiting for the user?)");
            }
            if (error != null)
            {
                if (error is CliException) throw error;
                throw new Exception(error.Message, error);
            }
            return result;
        }
    }
}
