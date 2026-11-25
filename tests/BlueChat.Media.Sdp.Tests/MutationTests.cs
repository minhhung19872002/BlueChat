using System.Text;

namespace BlueChat.Media.Sdp.Tests;

/// <summary>
/// Property-style robustness (spec principle 8): random byte and line mutations of real browser offers either parse
/// or throw <see cref="SdpParseException"/> — never anything else. When a mutant parses, what we write parses again
/// to the same text (the writer never produces SDP our own parser refuses).
/// </summary>
public class MutationTests
{
    [Theory]
    [MemberData(nameof(Corpus.Files), MemberType = typeof(Corpus))]
    public void Random_mutations_only_ever_raise_SdpParseException(string file)
    {
        var original = Encoding.UTF8.GetBytes(Corpus.Read(file));
        var rng = new Random(file.Sum(c => c) ^ 0x5D1); // deterministic per file
        var parsed = 0;
        for (var i = 0; i < 1500; i++)
        {
            var mutant = Mutate(original, rng);
            SdpSession? s = null;
            try
            {
                s = SdpParser.Parse(mutant);
            }
            catch (SdpParseException)
            {
                continue;
            }

            parsed++;
            var written = s.Write();
            var again = SdpParser.Parse(written);
            again.Write().Should().Be(written, "mutant #{0} of {1} must reach a fixed point", i, file);
        }

        parsed.Should().BeGreaterThan(0, "some mutations (e.g. changed digits) stay valid");
    }

    [Fact]
    public void Answering_mutated_offers_throws_only_documented_exceptions()
    {
        var rng = new Random(77);
        foreach (var f in Corpus.Files().Select(x => (string)x[0]))
        {
            var original = Encoding.UTF8.GetBytes(Corpus.Read(f));
            for (var i = 0; i < 300; i++)
            {
                SdpSession offer;
                try
                {
                    offer = SdpParser.Parse(Mutate(original, rng));
                }
                catch (SdpParseException)
                {
                    continue;
                }

                try
                {
                    var answer = JsepAnswerer.CreateAnswer(offer, new SdpAnswerOptions
                    {
                        IceUfrag = "abcd", IcePwd = "abcdefghijklmnopqrstuv", Fingerprint = new SdpFingerprint("sha-256", "00:11"),
                    });
                    SdpParser.Parse(answer.Write());
                }
                catch (SdpNegotiationException)
                {
                }
            }
        }
    }

    private static byte[] Mutate(byte[] input, Random rng)
    {
        var data = new List<byte>(input);
        var ops = rng.Next(1, 4);
        for (var k = 0; k < ops && data.Count > 0; k++)
        {
            var pos = rng.Next(data.Count);
            switch (rng.Next(7))
            {
                case 0:
                    data[pos] = (byte)rng.Next(256);
                    break;
                case 1:
                    data[pos] = (byte)"0123456789 :=/;,~-"[rng.Next(18)];
                    break;
                case 2:
                    data.RemoveAt(pos);
                    break;
                case 3:
                    data.Insert(pos, (byte)rng.Next(32, 127));
                    break;
                case 4:
                    data.RemoveRange(pos, data.Count - pos); // truncate
                    break;
                case 5:
                {
                    // duplicate a whole line
                    var lines = Encoding.UTF8.GetString(data.ToArray()).Split("\r\n").ToList();
                    var l = rng.Next(lines.Count);
                    lines.Insert(l, lines[l]);
                    data = new List<byte>(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
                    break;
                }

                default:
                {
                    // delete a whole line
                    var lines = Encoding.UTF8.GetString(data.ToArray()).Split("\r\n").ToList();
                    lines.RemoveAt(rng.Next(lines.Count));
                    data = new List<byte>(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
                    break;
                }
            }
        }

        return data.ToArray();
    }
}
