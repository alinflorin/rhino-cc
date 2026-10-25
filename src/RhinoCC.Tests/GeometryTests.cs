using System;
using System.Linq;
using RhinoCC.Core.Geometry;
using Xunit;

namespace RhinoCC.Tests
{
    public class GeometryTests
    {
        [Fact]
        public void HullOfASquareWithInnerPoints()
        {
            var pts = new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 3.0), (0.0, 3.0), (2.0, 1.0), (1.0, 2.0) };
            var hull = Hull2D.Hull(pts);
            Assert.Equal(4, hull.Count);
            Assert.Equal(12, Hull2D.Area(hull), 9);
        }

        [Fact]
        public void CollinearPointsHaveNoArea()
        {
            var hull = Hull2D.Hull(new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0), (3.0, 3.0) });
            Assert.Equal(0, Hull2D.Area(hull), 9);
            Assert.Equal(Math.Sqrt(18), Hull2D.Extent(new[] { (0.0, 0.0), (1.0, 1.0), (3.0, 3.0) }), 9);
        }

        [Fact]
        public void MatrixParsingAndChecks()
        {
            var r = Rotation.Parse("0,-1,0,1,0,0,0,0,1");
            Rotation.RequireRotation(r);
            Assert.Equal(new double[] { 0, 1, 0, -1, 0, 0, 0, 0, 1 }, Rotation.Transpose(r));
            Assert.Throws<ArgumentException>(() => Rotation.Parse("1,0,0"));
            Assert.Throws<ArgumentException>(() => Rotation.RequireRotation(Rotation.Parse("2,0,0,0,1,0,0,0,1")));
            Assert.Throws<ArgumentException>(() => Rotation.RequireRotation(Rotation.Parse("-1,0,0,0,1,0,0,0,1")));
        }

        [Fact]
        public void SnapFrameFollowsHints()
        {
            // a frame tilted 10 degrees about z snaps back to the world axes
            double c = Math.Cos(0.17), s = Math.Sin(0.17);
            var f = Rotation.SnapFrame(new[] { c, s, 0 }, new[] { -s, c, 0 }, new[] { 0.0, 0, 1 }, null, null);
            Assert.Equal(new double[] { 1, 0, 0 }, f[0]);
            Assert.Equal(new double[] { 0, 1, 0 }, f[1]);
            Assert.Equal(new double[] { 0, 0, 1 }, f[2]);
            // z pointing down, x along -y: y = z cross x stays right-handed
            var g = Rotation.SnapFrame(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, "-z", "-y");
            Assert.Equal(new double[] { 0, -1, 0 }, g[0]);
            Assert.Equal(new double[] { 0, 0, -1 }, g[2]);
            Assert.Equal(new double[] { -1, 0, 0 }, g[1]);
            Assert.Throws<ArgumentException>(() => Rotation.SnapFrame(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, "up", null));
        }

        [Fact]
        public void ComponentsGroupJoinedNodes()
        {
            var groups = Components.Of(6, new[] { (0, 1), (1, 2), (4, 5) });
            Assert.Equal(3, groups.Count);
            Assert.Equal(new[] { 0, 1, 2 }, groups[0].OrderBy(i => i).ToArray());
            Assert.Equal(new[] { 3 }, groups[2].ToArray());
        }
    }
}
