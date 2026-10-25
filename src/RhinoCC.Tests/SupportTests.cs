using System;
using System.Collections.Generic;
using System.Linq;
using RhinoCC.Core.Geometry;
using RhinoCC.Core.Printing;
using Xunit;

namespace RhinoCC.Tests
{
    public class SupportTests
    {
        /// <summary>A closed prism over a regular polygon, outward normals.</summary>
        private static TriMesh Prism(double r, double z0, double z1, int n, List<Vec3> v, List<int> t)
        {
            var b = v.Count;
            for (var i = 0; i < n; i++) v.Add(new Vec3(r * Math.Cos(2 * Math.PI * i / n), r * Math.Sin(2 * Math.PI * i / n), z0));
            for (var i = 0; i < n; i++) v.Add(new Vec3(r * Math.Cos(2 * Math.PI * i / n), r * Math.Sin(2 * Math.PI * i / n), z1));
            for (var i = 1; i < n - 1; i++)
            {
                t.AddRange(new[] { b, b + i + 1, b + i });
                t.AddRange(new[] { b + n, b + n + i, b + n + i + 1 });
            }
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                t.AddRange(new[] { b + i, b + j, b + n + j });
                t.AddRange(new[] { b + i, b + n + j, b + n + i });
            }
            return null;
        }

        /// <summary>A flat plate 1 mm thick, 5 mm above the plane, on a thin pole: an overhang with room below.</summary>
        private static TriMesh Mushroom()
        {
            var v = new List<Vec3>(); var t = new List<int>();
            Prism(5, 5, 6, 24, v, t);
            Prism(1, 0, 5, 12, v, t);
            return new TriMesh(v.ToArray(), t.ToArray());
        }

        private static PrintParams Params(double tilt) => new PrintParams { Tilt = tilt, Lift = 3, Tip = 0.2, Base = 0.8, Raft = 1, RaftRadius = 3 };

        [Fact]
        public void TiltRoundTrips()
        {
            var p = new Vec3(1, 2, 3);
            var q = Supports.Untilt(Supports.Tilt(p, 33), 33);
            Assert.True(p.DistanceTo(q) < 1e-9);
            var up = Supports.Up(30);
            Assert.Equal(1, up.Length, 9);
            Assert.True(Supports.Tilt(up, 30).DistanceTo(new Vec3(0, 0, 1)) < 1e-9);
        }

        [Fact]
        public void PlateOverhangGetsSupportsUnderIt()
        {
            var r = Supports.Auto(Mushroom(), Params(0), new AutoParams(), new Support[0], 1);
            Assert.True(r.Stats.Added >= 2, r.Stats.Added.ToString());
            // contacts sit on downward faces only: the plate's underside (z = 5) or the pole's foot (z = 0)
            Assert.All(r.Supports, s => Assert.True(Math.Abs(s.P.Z) < 0.1 || Math.Abs(s.P.Z - 5) < 0.1, s.P.ToString()));
            Assert.Contains(r.Supports, s => Math.Abs(s.P.Z - 5) < 0.1);
        }

        [Fact]
        public void ManualSupportsAreKeptAndCountForCoverage()
        {
            var manual = new[] { new Support { Id = 1, P = new Vec3(0, 0, 5), Source = "manual" } };
            var with = Supports.Auto(Mushroom(), Params(0), new AutoParams(), manual, 2);
            var without = Supports.Auto(Mushroom(), Params(0), new AutoParams(), new Support[0], 1);
            Assert.Equal("manual", with.Supports[0].Source);
            Assert.True(with.Stats.Added <= without.Stats.Added);
            Assert.Equal(2, with.Supports.Skip(1).Min(s => s.Id));
        }

        [Fact]
        public void PlanHasPillarsReachingThePlateAndARaft()
        {
            var m = Mushroom();
            var r = Supports.Auto(m, Params(0), new AutoParams(), new Support[0], 1);
            var plan = Supports.Plan(m, r.Supports, Params(0));
            Assert.Equal(r.Supports.Count, plan.Pillars.Count);
            Assert.Equal(r.Supports.Count, plan.Raft.Count);
            // the lowest point of the piece is 3 mm (lift) above the plate, and pillars end at the raft: below that
            Assert.All(plan.Pillars, p => Assert.True(p.Bottom.Z < 0 && p.Bottom.Z > -3, p.Bottom.ToString()));
            Assert.All(plan.Pillars, p => Assert.True(p.Top.Z > p.Bottom.Z));
        }

        [Fact]
        public void NoRaftWhenThicknessIsZero()
        {
            var m = Mushroom();
            var prm = Params(0); prm.Raft = 0;
            var r = Supports.Auto(m, prm, new AutoParams(), new Support[0], 1);
            Assert.Empty(Supports.Plan(m, r.Supports, prm).Raft);
        }

        [Fact]
        public void TiltedPillarsFollowThePrintUpAxis()
        {
            var m = Mushroom();
            var prm = Params(30);
            var r = Supports.Auto(m, prm, new AutoParams(), new Support[0], 1);
            var plan = Supports.Plan(m, r.Supports, prm);
            Assert.NotEmpty(plan.Pillars);
            foreach (var p in plan.Pillars)
            {
                var d = (p.Top - p.Bottom).Unit();
                Assert.True(d.DistanceTo(plan.Up) < 1e-6, $"{d} vs {plan.Up}");
            }
        }

        [Fact]
        public void TiltRankingPrefersFewerOverhangs()
        {
            var rows = Supports.RankTilts(Mushroom(), 0, 60, 15, 45, 3);
            Assert.Equal(5, rows.Count);
            Assert.True(rows[0].Overhang <= rows.Last().Overhang);
        }

        [Fact]
        public void FrameSaysWhetherThePieceFits()
        {
            var f = Supports.Frame(Mushroom(), 0, 3);
            Assert.True(f.Fits);
            Assert.Equal(9, f.Size.Z, 6); // 6 high + 3 lift
            Assert.Equal(3, f.Zoff, 6);
        }

        [Fact]
        public void SprueGoesOnASurfaceWithFreeAirAndCanBePlacedByHand()
        {
            var m = Mushroom();
            var auto = Sprue.Plan(m, 2, 5, null);
            Assert.NotNull(auto);
            Assert.False(auto.Manual);
            Assert.True(auto.Normal.Length > 0.99);
            // by hand: the closest point of the surface to a point in the air above the plate
            var hand = Sprue.Plan(m, 2, 5, new Vec3(0, 0, 9));
            Assert.True(hand.Manual);
            Assert.Equal(6, hand.At.Z, 6);
            Assert.Equal(1, hand.Normal.Z, 6);
            Assert.Equal(1, hand.Thickness, 3); // the plate is 1 mm
        }

        [Fact]
        public void SprueRatioRange()
        {
            Assert.True(Sprue.GoodRatio(2, 2.5, out var r));
            Assert.Equal(0.8, r, 9);
            Assert.False(Sprue.GoodRatio(2, 0.5, out _));
        }

        [Fact]
        public void SettingsRoundTripAndValidate()
        {
            var s = new PrintSettings { Metal = "18k", Tilt = 45 };
            s.Supports.Enabled = true;
            s.Sprue.At = new[] { 1.0, 2.0, 3.0 };
            var back = PrintSettings.Parse(s.ToJson());
            Assert.Equal("18k", back.Metal);
            Assert.True(back.Supports.Enabled);
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, back.Sprue.At);
            Assert.Null(back.Validate());
            Assert.NotNull(new PrintSettings { Metal = "gold-ish" }.Validate());
            var bad = new PrintSettings(); bad.Supports.RaftThickness = 3; // lift is 3
            Assert.Contains("raft thickness", bad.Validate());
            Assert.Equal(30, PrintSettings.Parse("not json").Tilt);
            Assert.Equal(30, PrintSettings.Parse("{\"Supports\":null}").Tilt);
        }
    }
}
