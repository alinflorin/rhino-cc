using System.Collections.Generic;
using System.Linq;
using RhinoCC.Core.Printing;
using Xunit;

namespace RhinoCC.Tests
{
    public class PrintingTests
    {
        [Fact]
        public void WeightFromVolume()
        {
            // 1 cm³ of sterling
            Assert.Equal(10.4, Metals.Grams(1000, Metals.Lookup("Sterling")), 6);
            Assert.Throws<System.ArgumentException>(() => Metals.Lookup("unobtainium"));
        }

        [Fact]
        public void BuildVolumeAllowsTurningThePiece()
        {
            Assert.True(BuildVolume.FitsAsIs(100, 50, 100));
            Assert.False(BuildVolume.FitsAsIs(100, 100, 50));
            Assert.True(BuildVolume.Fits(100, 100, 50));
            Assert.False(BuildVolume.Fits(100, 100, 100));
        }

        // a box 10 x 10 x 10 as 12 triangles is enough for the area logic: only the bottom faces down
        private static List<Facet> Cube()
        {
            var f = new List<Facet>();
            void Quad(double nx, double ny, double nz, double cx, double cy, double cz)
            {
                f.Add(new Facet(nx, ny, nz, 50, cx, cy, cz));
                f.Add(new Facet(nx, ny, nz, 50, cx, cy, cz));
            }
            Quad(0, 0, -1, 5, 5, 0); Quad(0, 0, 1, 5, 5, 10);
            Quad(1, 0, 0, 10, 5, 5); Quad(-1, 0, 0, 0, 5, 5);
            Quad(0, 1, 0, 5, 10, 5); Quad(0, -1, 0, 5, 0, 5);
            return f;
        }

        [Fact]
        public void FlatBottomOnThePlateNeedsNoSupport()
        {
            Assert.Equal(0, Overhang.Fraction(Cube(), 0, "x", 45), 6);
        }

        [Fact]
        public void TiltedCubeOverhangsAndSearchFindsTheFlatOrientation()
        {
            var cube = Cube();
            Assert.True(Overhang.Fraction(cube, 30, "x", 45) > 0);
            var best = Overhang.TiltSearch(cube, 45)[0];
            Assert.Equal(0, best.Fraction, 6);
            Assert.Equal(0, best.Degrees, 6);
        }

        [Fact]
        public void OverhangPlatformFacingDownIsCounted()
        {
            // a plate on the bottom plus a ledge facing down at mid height
            var faces = Cube().Concat(new[] { new Facet(0, 0, -1, 25, 5, 5, 5) }).ToList();
            var share = Overhang.Fraction(faces, 0, "x", 45);
            Assert.Equal(25.0 / 625.0, share, 6);
        }
    }
}
