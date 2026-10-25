using System;
using Newtonsoft.Json;

namespace RhinoCC.Core.Printing
{
    /// <summary>
    /// The settings of the open model (what frederic-coultier calls project settings): material, print orientation,
    /// supports and sprue. They are stored in the .3dm; every size is in millimetres whatever the model's units are.
    /// </summary>
    public sealed class PrintSettings
    {
        public string Notes { get; set; } = "";
        /// <summary>A key of <see cref="Metals"/>.</summary>
        public string Metal { get; set; } = "sterling";
        /// <summary>"casting" (a master for lost-wax casting) or "direct" (the resin is the piece).</summary>
        public string Process { get; set; } = "casting";
        public string Printer { get; set; } = "Elegoo Mars 5";
        /// <summary>The thinnest wall `analyze` accepts by default.</summary>
        public double MinWall { get; set; } = 0.8;
        /// <summary>Print orientation: the model is turned this many degrees about X before it sits on the plate.</summary>
        public double Tilt { get; set; } = 30;
        public SupportSettings Supports { get; set; } = new SupportSettings();
        public SprueSettings Sprue { get; set; } = new SprueSettings();

        public static readonly string[] Processes = { "casting", "direct" };

        public static PrintSettings Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new PrintSettings();
            try { return Normalized(JsonConvert.DeserializeObject<PrintSettings>(json)); }
            catch (JsonException) { return new PrintSettings(); }
        }

        public string ToJson() => JsonConvert.SerializeObject(this);

        public PrintSettings Clone() => Parse(ToJson());

        private static PrintSettings Normalized(PrintSettings s)
        {
            s = s ?? new PrintSettings();
            s.Supports = s.Supports ?? new SupportSettings();
            s.Sprue = s.Sprue ?? new SprueSettings();
            s.Notes = s.Notes ?? "";
            return s;
        }

        /// <summary>The first problem with the values, or null. Used by the settings dialog and by the commands that set values.</summary>
        public string Validate()
        {
            if (!Metals.Density.ContainsKey(Metal ?? "")) return $"metal '{Metal}' is unknown (known: {Metals.Names})";
            if (Array.IndexOf(Processes, Process) < 0) return "process must be casting or direct";
            if (!(MinWall > 0 && MinWall <= 5)) return "min wall must be between 0 and 5 mm";
            if (!(Tilt >= -90 && Tilt <= 90)) return "tilt must be between -90 and 90 degrees";
            return Supports.Validate() ?? Sprue.Validate();
        }
    }

    public sealed class SupportSettings
    {
        public bool Enabled { get; set; }
        /// <summary>Radius where a pillar touches the piece.</summary>
        public double TipRadius { get; set; } = 0.2;
        /// <summary>Radius where a pillar meets the plate or raft.</summary>
        public double BaseRadius { get; set; } = 0.5;
        /// <summary>Faces pointing down within this many degrees of horizontal need support.</summary>
        public double OverhangAngle { get; set; } = 45;
        /// <summary>Largest distance one automatic support may serve.</summary>
        public double Spacing { get; set; } = 4;
        /// <summary>How far the raft reaches around each pillar.</summary>
        public double RaftRadius { get; set; } = 3;
        /// <summary>0 = no raft. Must stay below <see cref="Lift"/>.</summary>
        public double RaftThickness { get; set; } = 1;
        /// <summary>Gap between the lowest point of the piece and the plate.</summary>
        public double Lift { get; set; } = 3;

        public string Validate()
        {
            if (!(TipRadius >= 0.1 && TipRadius <= 1)) return "tip radius must be between 0.1 and 1 mm";
            if (!(BaseRadius >= 0.2 && BaseRadius <= 2)) return "base radius must be between 0.2 and 2 mm";
            if (!(OverhangAngle >= 0 && OverhangAngle <= 80)) return "overhang angle must be between 0 and 80 degrees";
            if (!(Spacing >= 1 && Spacing <= 10)) return "spacing must be between 1 and 10 mm";
            if (!(RaftRadius >= 0 && RaftRadius <= 10)) return "raft radius must be between 0 and 10 mm";
            if (!(Lift >= 1 && Lift <= 10)) return "lift must be between 1 and 10 mm";
            if (!(RaftThickness >= 0 && RaftThickness <= Lift - 0.1)) return $"raft thickness ({RaftThickness}) must be between 0 and lift - 0.1 ({Lift - 0.1:0.##}) mm";
            return null;
        }
    }

    public sealed class SprueSettings
    {
        public bool Enabled { get; set; }
        public double Diameter { get; set; } = 2;
        public double Length { get; set; } = 5;
        /// <summary>A manual spot in model coordinates (the model's own units); null = placed automatically.</summary>
        public double[] At { get; set; }

        public string Validate()
        {
            if (!(Diameter >= 0.5 && Diameter <= 6)) return "sprue diameter must be between 0.5 and 6 mm";
            if (!(Length >= 1 && Length <= 30)) return "sprue length must be between 1 and 30 mm";
            if (At != null && At.Length != 3) return "sprue position needs x,y,z";
            return null;
        }
    }
}
