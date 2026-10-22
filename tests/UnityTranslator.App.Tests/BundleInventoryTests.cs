using System.Text;
using Vesper.Packaging;

namespace UnityTranslator.App.Tests;

public sealed class BundleInventoryTests
{
    // These tests catch skipped validation and hashing of the wrong payload range.
    [Fact]
    public void ReadsAndHashesOriginalPayloadBytes()
    {
        WithFixture(Create(), path =>
        {
            var result = BundleInventory.Read(path);
            var entry = Assert.Single(result.Entries);
            Assert.Equal("fixture.dll", entry.Path);
            Assert.Equal(512, entry.Offset);
            Assert.Equal(3, entry.Size);
            Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", entry.Sha256);
            Assert.Equal(1024, result.HeaderOffset);
        });
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("duplicate-marker")]
    [InlineData("wrong-machine")]
    [InlineData("wrong-version")]
    [InlineData("invalid-offset")]
    [InlineData("count")]
    [InlineData("flags")]
    [InlineData("compressed")]
    [InlineData("out-of-range")]
    [InlineData("overflow")]
    [InlineData("overlap")]
    [InlineData("duplicate-path")]
    [InlineData("truncated")]
    [InlineData("bad-utf8")]
    [InlineData("overlong-length")]
    [InlineData("bad-type")]
    [InlineData("cached-json")]
    public void RejectsMalformedBundle(string defect)
    {
        WithFixture(Create(defect), path => Assert.ThrowsAny<InvalidDataException>(() => BundleInventory.Read(path)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/root")]
    [InlineData("C:/drive")]
    [InlineData("a\\..\\b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("CON.dll")]
    [InlineData("a. /b")]
    [InlineData("nul")]
    [InlineData("a:stream")]
    public void RejectsUnsafeWindowsPayloadNames(string name)
    {
        WithFixture(Create(name: name), path => Assert.ThrowsAny<InvalidDataException>(() => BundleInventory.Read(path)));
    }

    [Fact]
    public void NormalizesWindowsBundleSeparatorsWithoutChangingOriginalName()
    {
        WithFixture(Create(name: "fr\\fixture.resources.dll"), path =>
        {
            var entry = Assert.Single(BundleInventory.Read(path).Entries);
            Assert.Equal("fr/fixture.resources.dll", entry.Path);
            Assert.Equal("fr\\fixture.resources.dll", entry.RawPath);
        });
    }

    private static byte[] Create(string defect = "", string name = "fixture.dll")
    {
        using var stream = new MemoryStream(new byte[1600], true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)0x5a4d);
        stream.Position = 0x3c; writer.Write(0x80);
        stream.Position = 0x80; writer.Write(0x4550); writer.Write((ushort)(defect == "wrong-machine" ? 0x14c : 0x8664));
        stream.Position = 0x98; writer.Write((ushort)0x20b);
        var marker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
        stream.Position = 248; writer.Write(defect == "invalid-offset" ? long.MaxValue : 1024L);
        if (defect != "missing-marker") writer.Write(marker);
        if (defect == "duplicate-marker") { stream.Position = 350; writer.Write(marker); }
        stream.Position = 512; writer.Write(Encoding.ASCII.GetBytes("abc"));
        stream.Position = 1024; writer.Write(defect == "wrong-version" ? 7 : 6); writer.Write(0);
        writer.Write(defect == "count" ? int.MaxValue : defect is "overlap" or "duplicate-path" ? 2 : 1);
        writer.Write("id"); writer.Write(defect == "cached-json" ? 512L : 0L); writer.Write(defect == "cached-json" ? 3L : 0L);
        writer.Write(0L); writer.Write(0L); writer.Write(defect == "flags" ? 2UL : 0UL);
        WriteEntry(name, 512);
        if (defect is "overlap" or "duplicate-path") WriteEntry(defect == "duplicate-path" ? "FIXTURE.DLL" : "other.dll", defect == "overlap" ? 513 : 600);
        var length = (int)stream.Position;
        var bytes = stream.ToArray()[..length];
        return defect == "truncated" ? bytes[..^2] : bytes;

        void WriteEntry(string path, long offset)
        {
            writer.Write(defect == "out-of-range" ? 1599L : offset);
            writer.Write(defect == "overflow" ? long.MaxValue : 3L);
            writer.Write(defect == "compressed" ? 2L : 0L);
            writer.Write((byte)(defect == "bad-type" ? 9 : 1));
            if (defect == "bad-utf8") writer.Write(new byte[] { 1, 0xff });
            else if (defect == "overlong-length") writer.Write(new byte[] { 0x81, 0, 65 });
            else writer.Write(path);
        }
    }

    private static void WithFixture(byte[] bytes, Action<string> check)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vesper-bundle-test-" + Guid.NewGuid() + ".exe");
        File.WriteAllBytes(path, bytes);
        try { check(path); } finally { File.Delete(path); }
    }
}
