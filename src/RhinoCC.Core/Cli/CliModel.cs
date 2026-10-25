using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoCC.Core.Cli
{
    public sealed class CliImage
    {
        public string Mime { get; set; }
        public string Base64 { get; set; }
    }

    public sealed class CliResult
    {
        public string Text { get; set; } = "";
        public List<CliImage> Images { get; } = new List<CliImage>();
        public bool IsError { get; set; }

        public static CliResult Ok(string text) => new CliResult { Text = text ?? "" };

        public static CliResult Fail(string text) => new CliResult { Text = text ?? "", IsError = true };
    }

    /// <summary>A mistake in how a command was called; the message goes straight back to the agent.</summary>
    public sealed class CliException : Exception
    {
        public CliException(string message) : base(message) { }
    }

    public sealed class OptionSpec
    {
        public string Name { get; }
        /// <summary>Placeholder for the value in help, or null for a flag.</summary>
        public string Value { get; }
        public string Description { get; }

        public OptionSpec(string name, string value, string description)
        {
            Name = name;
            Value = value;
            Description = description;
        }

        public bool IsFlag => Value == null;
    }

    public sealed class CommandSpec
    {
        public string Name { get; }
        public string Group { get; }
        public string Usage { get; }
        public string Summary { get; }
        public List<OptionSpec> Options { get; } = new List<OptionSpec>();
        public List<string> Examples { get; } = new List<string>();
        public Func<CliArgs, CliResult> Handler { get; }

        public CommandSpec(string name, string group, string usage, string summary, Func<CliArgs, CliResult> handler)
        {
            Name = name;
            Group = group;
            Usage = usage;
            Summary = summary;
            Handler = handler;
        }

        public CommandSpec Opt(string name, string value, string description)
        {
            Options.Add(new OptionSpec(name, value, description));
            return this;
        }

        public CommandSpec Flag(string name, string description) => Opt(name, null, description);

        public CommandSpec Example(string example)
        {
            Examples.Add(example);
            return this;
        }
    }

    public sealed class CliArgs
    {
        private readonly Dictionary<string, string> _options = new Dictionary<string, string>();

        public List<string> Positional { get; } = new List<string>();

        internal void SetOption(string name, string value) => _options[name] = value;

        public bool Has(string name) => _options.ContainsKey(name);

        public string Opt(string name) => _options.TryGetValue(name, out var v) ? v : null;

        public string Pos(int index, string what)
        {
            if (index >= Positional.Count) throw new CliException("missing " + what);
            return Positional[index];
        }

        public static double Number(string text, string what)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw new CliException($"{what}: '{text}' is not a number");
        }

        public double PosNumber(int index, string what) => Number(Pos(index, what), what);

        public double? OptNumber(string name) => Has(name) ? Number(Opt(name), "--" + name) : (double?)null;
    }
}
