using Circuit;
using ComputerAlgebra;
using Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace livespice_cli
{
    class Program
    {
        static void Main(string[] args)
        {
            string inputPath = null, outputPath = null, circuitPath = null, paramsStr = null, speakerName = null;
            string netlistPath = null, jobsPath = null;
            int sampleRate = 48000, oversample = 2, iterations = 8;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input": inputPath = args[++i]; break;
                    case "--output": outputPath = args[++i]; break;
                    case "--circuit": circuitPath = args[++i]; break;
                    case "--params": paramsStr = args[++i]; break;
                    case "--speaker": speakerName = args[++i]; break;
                    case "--sr": sampleRate = int.Parse(args[++i]); break;
                    case "--oversample": oversample = int.Parse(args[++i]); break;
                    case "--iterations": iterations = int.Parse(args[++i]); break;
                    // Dump the authoritative netlist (components + node connectivity) as JSON
                    // and exit — the foundation for the ngspice-backend .schx translator.
                    case "--netlist": netlistPath = args[++i]; break;
                    // Batch mode: JSONL, one render job per line, one process for all of them.
                    // The schematic parse and input-WAV decode amortise; the per-job symbolic
                    // solve cannot (pot values are constant-folded into the equations, which is
                    // exactly why the solved system is small and fast), but a warm JIT makes the
                    // second and later solves markedly cheaper than a cold process each.
                    case "--jobs": jobsPath = args[++i]; break;
                }
            }

            // --netlist only needs --circuit; --jobs supplies audio paths per job.
            if (circuitPath == null ||
                (netlistPath == null && jobsPath == null && (inputPath == null || outputPath == null)))
            {
                Console.Error.WriteLine("Usage: livespice_cli --input in.wav --output out.wav --circuit circuit.schx [--params k=v,...] [--speaker S1] [--sr 48000] [--oversample 2] [--iterations 8]");
                Console.Error.WriteLine("   or: livespice_cli --circuit circuit.schx --netlist out.json   (dump netlist and exit)");
                Console.Error.WriteLine("   or: livespice_cli --circuit circuit.schx --jobs jobs.jsonl   (batch render; '-' = stdin)");
                Console.Error.WriteLine("       each line: {\"output\": ..., \"params\": \"k=v,...\", \"input\": ..., \"oversample\": N, \"iterations\": N, \"speaker\": ...}");
                Console.Error.WriteLine("       omitted job fields inherit the corresponding CLI flag. Result lines on stdout: JOB <n> OK|FAIL ...");
                Environment.Exit(1);
            }

            var log = new ConsoleLog { Verbosity = MessageType.Warning };

            // load circuit
            //
            // NORMALISE THE MICRO SIGN FIRST, or every microfarad silently becomes a FARAD.
            //
            // There are two characters that look like "micro" and LiveSPICE's Quantity parser only
            // knows one of them:
            //
            //     U+03BC GREEK SMALL LETTER MU   parsed as micro   (also what ASCII 'u' aliases to)
            //     U+00B5 MICRO SIGN              NOT a known prefix -> the prefix is DROPPED
            //
            // So "4.7µF" written with U+00B5 parses as 4.7 FARADS. It does not throw. It does not
            // warn. The circuit builds, the simulation runs, and every capacitor in it is a dead
            // short at audio frequencies -- the output is confidently, quietly wrong.
            //
            // Our .schx library happens to use U+03BC today (checked: 6 of 23 files, 0 with U+00B5),
            // which is why nobody has been bitten. But U+00B5 is what most editors and tools emit,
            // so this is one save-from-the-wrong-tool away from corrupting a dataset. It used to be
            // patched into a vendored LiveSPICE fork -- which cannot work here, because the ORACLE
            // must build against PRISTINE upstream (an oracle built from the thing under test is not
            // an oracle). Normalising the input costs nothing and needs no fork.
            string schxText = File.ReadAllText(circuitPath);
            if (schxText.IndexOf('µ') >= 0)
            {
                Console.Error.WriteLine(
                    "note: normalising U+00B5 MICRO SIGN -> U+03BC GREEK MU (upstream would read 'µF' as FARADS)");
                string tmp = Path.Combine(Path.GetTempPath(),
                    $"lsp_{Path.GetFileNameWithoutExtension(circuitPath)}_{Guid.NewGuid():N}.schx");
                File.WriteAllText(tmp, schxText.Replace('µ', 'μ'));
                circuitPath = tmp;
            }

            var schematic = Schematic.Load(circuitPath, log);
            var circuit = schematic.Build(log);

            if (netlistPath != null)
            {
                DumpNetlist(circuit, netlistPath);
                return;
            }

            if (jobsPath != null)
            {
                Environment.Exit(RunJobs(schematic, log, jobsPath, inputPath, speakerName,
                                         sampleRate, oversample, iterations));
            }

            try
            {
                ApplyParams(circuit, paramsStr);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Environment.Exit(2);
            }

            Expression outputExpr;
            try
            {
                outputExpr = ResolveOutput(circuit, speakerName);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Environment.Exit(1);
                return;
            }

            // read input WAV
            var (inSamples, inSampleRate) = ReadWav(inputPath);
            int N = inSamples.Length;
            int outSampleRate = sampleRate > 0 ? sampleRate : inSampleRate;

            double[] outputBuffer = Render(circuit, outputExpr, inSamples, outSampleRate,
                                           oversample, iterations, log);

            // write output WAV
            WriteWavFloat(outputPath, outputBuffer, outSampleRate);

            double dur = (double)N / outSampleRate;
            Console.Error.WriteLine($"Input:    {inputPath}");
            Console.Error.WriteLine($"Format:   {outSampleRate} Hz, 1 ch, 32-bit float  →  {dur:F3} sec mono");
            Console.Error.WriteLine($"Circuit:  {circuit.Name}  (oversample {oversample}x)");
            Console.Error.WriteLine($"Output:   {outputPath}  ({N} samples, 32-bit float mono)");
        }

        // Set knob parameters.
        //
        // A --params name that matches NOTHING used to be silently ignored: we rendered at the
        // circuit's defaults and exited 0. That is the worst possible failure mode for a dataset
        // generator -- a typo'd or renamed knob yields N permutations that are all IDENTICAL,
        // with no error anywhere, and the trainer then dutifully learns that the knob does
        // nothing. Nothing downstream can detect it: the audio is valid, the RMS is normal, the
        // ESR is fine. So: match or throw (the callers decide how to die).
        static void ApplyParams(Circuit.Circuit circuit, string paramsStr)
        {
            if (paramsStr != null)
            {
                var unmatched = new List<string>();
                foreach (var kv in paramsStr.Split(','))
                {
                    var parts = kv.Split('=');
                    if (parts.Length != 2)
                        throw new ArgumentException($"error: malformed --params entry '{kv}' (want name=value)");
                    var name = parts[0].Trim();
                    var value = double.Parse(parts[1].Trim());

                    // A CONTROL IS NOT A COMPONENT.
                    //
                    // A ganged (multi-section) pot is ONE knob with SEVERAL wipers, and LiveSPICE
                    // models it as several IPotControl components sharing a GROUP. Upstream's own
                    // VST resolves it that way (LiveSPICEVst/SimulationProcessor.cs): a pot with no
                    // Group is its own control named by Name; pots WITH a Group collapse into one
                    // control named by the GROUP, and setting it writes PotValue to every section.
                    //
                    // We used to match on Name alone, which is not the same thing and got the
                    // Dumble wrong: its 'Clean Master' and 'OD Master' share Group "Master", so
                    // LiveSPICE shows ONE Master knob driving both. Matching by name let a caller
                    // move one section and leave the other parked -- a state the schematic says is
                    // unreachable, and exactly what the shipped Dumble dataset did.
                    //
                    // So: resolve the GROUP first, then fall back to the Name.
                    var all = circuit.Components.OfType<IPotControl>().Cast<Component>().ToList();

                    var pots = all
                        .Where(c => !string.IsNullOrWhiteSpace(((IGroupableComponent)c).Group)
                                 && ((IGroupableComponent)c).Group.Trim() == name)
                        .ToList();

                    if (pots.Count == 0)
                        pots = all
                            .Where(c => string.IsNullOrWhiteSpace(((IGroupableComponent)c).Group)
                                     && c.Name.Trim() == name)
                            .ToList();

                    if (pots.Count > 0)
                    {
                        foreach (var p in pots)
                            ((IPotControl)p).PotValue = value;
                        continue;
                    }

                    // Same for switches: set every matching section (ganged switches).
                    var switches = circuit.Components
                        .OfType<SinglePoleSwitch>()
                        .Where(c => c.Name.Trim() == name)
                        .ToList();

                    if (switches.Count > 0)
                    {
                        foreach (var sw in switches)
                            sw.Position = (int)value;
                        continue;
                    }

                    unmatched.Add(name);
                }

                if (unmatched.Count > 0)
                {
                    // List CONTROLS, not components: suggesting 'Clean Master' when the control is
                    // 'Master' would send the caller straight back into the error.
                    var haveP = circuit.Components.OfType<IPotControl>().Cast<Component>()
                        .Select(c => {
                            string g = ((IGroupableComponent)c).Group;
                            return !string.IsNullOrWhiteSpace(g) ? g.Trim() : c.Name.Trim();
                        })
                        .Distinct().OrderBy(s => s);
                    var haveS = circuit.Components.OfType<SinglePoleSwitch>()
                        .Select(c => c.Name.Trim()).Distinct().OrderBy(s => s);
                    throw new ArgumentException(
                        $"error: --params names not found in the circuit: {string.Join(", ", unmatched)}\n" +
                        $"  pots/rheostats: {string.Join(", ", haveP)}\n" +
                        $"  switches:       {string.Join(", ", haveS)}\n" +
                        "  (rendering at defaults instead would have produced identical permutations, silently)");
                }
            }
        }

        static Expression ResolveOutput(Circuit.Circuit circuit, string speakerName)
        {
            var allSpeakers = circuit.Components.OfType<Speaker>().ToList();
            List<Speaker> speakers;
            if (speakerName != null)
            {
                speakers = allSpeakers.Where(s => s.Name == speakerName).ToList();
                if (speakers.Count == 0)
                {
                    var names = string.Join(", ", allSpeakers.Select(s => s.Name));
                    throw new ArgumentException($"Speaker '{speakerName}' not found. Available: {names}");
                }
            }
            else
            {
                speakers = allSpeakers;
            }

            if (speakers.Count == 0)
            {
                // fallback: use first node voltage
                return circuit.Nodes.Select(n => n.V).FirstOrDefault() ?? 0;
            }
            var sum = (Expression)0;
            foreach (var s in speakers)
                sum += s.Out;
            return sum;
        }

        static double[] Render(Circuit.Circuit circuit, Expression outputExpr, double[] inSamples,
                               int outSampleRate, int oversample, int iterations, ConsoleLog log)
        {
            // find input component
            var inputExpr = circuit.Components
                .OfType<Input>()
                .Select(i => i.In)
                .DefaultIfEmpty("V[t]")
                .Single();

            // build simulation
            var ts = TransientSolution.Solve(
                circuit.Analyze(),
                (Real)1 / (outSampleRate * oversample),
                log);

            var sim = new Simulation(ts)
            {
                Oversample = oversample,
                Iterations = iterations,
                Input = new[] { inputExpr },
                Output = new[] { outputExpr },
            };

            // process in chunks
            int N = inSamples.Length;
            double[] outputBuffer = new double[N];
            int chunk = 4096;
            int offset = 0;
            while (offset < N)
            {
                int n = Math.Min(chunk, N - offset);
                double[] chunkIn = new double[n];
                Array.Copy(inSamples, offset, chunkIn, 0, n);
                double[] chunkOut = new double[n];
                sim.Run(chunkIn, chunkOut);
                Array.Copy(chunkOut, 0, outputBuffer, offset, n);
                offset += n;
            }
            return outputBuffer;
        }

        // -------------------------------------------------------------------
        // Batch mode: many renders of ONE schematic in ONE process.
        //
        // The per-invocation fixed cost -- process spawn, assembly load, schx
        // parse, input-WAV decode, cold-JIT of the symbolic solve -- is ~0.5 s
        // on a pedal, which rivals the render itself for the probe tools
        // (convergence audit, --oversample auto, grid adequacy) that fire
        // hundreds of short-clip renders at the same circuit. Here it is paid
        // once. The circuit is REBUILT from the schematic per job, so every
        // job starts from schematic defaults: a job that omits a knob cannot
        // inherit the previous job's setting.
        //
        // One line of JSONL per job; results stream to stdout as
        //   JOB <n> OK <output>       or       JOB <n> FAIL <one-line message>
        // and a failed job does not stop the batch (exit 3 if any failed).
        // -------------------------------------------------------------------
        static int RunJobs(Schematic schematic, ConsoleLog log, string jobsPath,
                           string defInput, string defSpeaker, int sampleRate,
                           int defOversample, int defIterations)
        {
            var wavCache = new Dictionary<string, (double[] samples, int sampleRate)>();
            int idx = 0, failed = 0;
            using var reader = jobsPath == "-"
                ? Console.In
                : new StreamReader(jobsPath);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                idx++;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string GetStr(string name, string dflt) =>
                        root.TryGetProperty(name, out var el) && el.ValueKind != System.Text.Json.JsonValueKind.Null
                            ? el.GetString() : dflt;
                    int GetInt(string name, int dflt) =>
                        root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? el.GetInt32() : dflt;

                    string output = GetStr("output", null);
                    string input = GetStr("input", defInput);
                    string prms = GetStr("params", null);
                    string speaker = GetStr("speaker", defSpeaker);
                    int os = GetInt("oversample", defOversample);
                    int iters = GetInt("iterations", defIterations);
                    if (output == null || input == null)
                        throw new ArgumentException("job needs \"output\" and \"input\" (or a global --input)");

                    if (!wavCache.TryGetValue(input, out var wav))
                    {
                        wav = ReadWav(input);
                        wavCache[input] = wav;
                    }
                    int outSampleRate = sampleRate > 0 ? sampleRate : wav.sampleRate;

                    var circuit = schematic.Build(log);
                    ApplyParams(circuit, prms);
                    var outputExpr = ResolveOutput(circuit, speaker);
                    var buf = Render(circuit, outputExpr, wav.samples, outSampleRate, os, iters, log);
                    WriteWavFloat(output, buf, outSampleRate);
                    Console.Out.WriteLine($"JOB {idx} OK {output}");
                }
                catch (Exception e)
                {
                    failed++;
                    // Include the exception TYPE: a SimulationDiverged with a bare message must
                    // still say "diverged" to the harness's convergence-failure matcher.
                    string msg = $"{e.GetType().Name}: {e.Message}".Replace('\n', ' ').Replace('\r', ' ');
                    Console.Out.WriteLine($"JOB {idx} FAIL {msg}");
                }
                Console.Out.Flush();
            }
            Console.Error.WriteLine($"jobs: {idx - failed}/{idx} OK");
            return failed == 0 ? 0 : 3;
        }

        static (double[] samples, int sampleRate) ReadWav(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            // RIFF header
            var riff = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (riff != "RIFF") throw new Exception("Not a RIFF file");
            br.ReadInt32(); // file size
            var wave = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (wave != "WAVE") throw new Exception("Not a WAVE file");

            int channels = 0, sampleRate = 0, bitsPerSample = 0;
            List<byte> data = null;

            while (fs.Position < fs.Length)
            {
                var chunkId = Encoding.ASCII.GetString(br.ReadBytes(4));
                int chunkSize = br.ReadInt32();

                if (chunkId == "fmt ")
                {
                    var audioFormat = br.ReadInt16(); // 1=PCM, 3=IEEE float
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadInt16(); // block align
                    bitsPerSample = br.ReadInt16();
                    if (chunkSize > 16)
                        br.ReadBytes(chunkSize - 16);
                }
                else if (chunkId == "data")
                {
                    data = new List<byte>(br.ReadBytes(chunkSize));
                }
                else
                {
                    br.ReadBytes(chunkSize);
                }
            }

            if (data == null) throw new Exception("No data chunk found");

            // Snapshot the data chunk to an array ONCE. Previously the 16- and 32-bit
            // branches called data.ToArray() per sample, copying the whole ~MB chunk on
            // every iteration — O(N^2), which made large 16/32-bit-float WAVs appear to
            // hang inside ReadWav (the 24-bit branch indexed directly and was unaffected).
            byte[] buf = data.ToArray();

            int bytesPerSample = bitsPerSample / 8;
            int totalSamples = buf.Length / bytesPerSample / channels;
            double[] samples = new double[totalSamples];

            for (int i = 0; i < totalSamples; i++)
            {
                double sample = 0;
                int byteOffset = i * channels * bytesPerSample;

                if (bitsPerSample == 16)
                {
                    short val = BitConverter.ToInt16(buf, byteOffset);
                    sample = val / 32768.0;
                }
                else if (bitsPerSample == 24)
                {
                    int val = buf[byteOffset] | (buf[byteOffset + 1] << 8) | (buf[byteOffset + 2] << 16);
                    if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                    sample = val / 8388608.0;
                }
                else if (bitsPerSample == 32)
                {
                    float val = BitConverter.ToSingle(buf, byteOffset);
                    sample = val;
                }
                else
                {
                    throw new Exception($"Unsupported bits per sample: {bitsPerSample}");
                }

                samples[i] = sample;
            }

            return (samples, sampleRate);
        }

        static void WriteWavFloat(string path, double[] samples, int sampleRate)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            int dataSize = samples.Length * 4;
            int fileSize = 36 + dataSize;

            // RIFF header
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(fileSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16); // chunk size
            bw.Write((short)3); // IEEE float
            bw.Write((short)1); // mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 4); // byte rate
            bw.Write((short)4); // block align
            bw.Write((short)32); // bits per sample

            // data chunk
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            foreach (var s in samples)
                bw.Write((float)s);
        }

        // -------------------------------------------------------------------
        // Netlist dump — authoritative components + node connectivity as JSON.
        // Consumed by the ngspice-backend .schx translator (batch_harness).
        // -------------------------------------------------------------------
        static string JsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // Emit the base-unit numeric value (e.g. 1e-06), NOT Quantity.ToString():
        // the LiveSPICE prefix formatter DROPS the micro sign, so "1 µF" prints as
        // "1 F" and the ngspice translator would read it as 1 FARAD. The explicit
        // (double) cast on Quantity gives the exact base-unit value.
        static string QNum(Quantity q)
        {
            return ((double)q).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string CompValue(Component c)
        {
            switch (c)
            {
                case Resistor r: return QNum(r.Resistance);
                case Capacitor cap: return QNum(cap.Capacitance);
                case Potentiometer p: return QNum(p.Resistance);
                case Rail rail: return QNum(rail.Voltage);
                case CenterTapTransformer tx: return tx.Turns.ToString();  // ratio "7:108", not a Quantity
                default: return null;
            }
        }

        static void DumpNetlist(Circuit.Circuit circuit, string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"components\": [\n");
            bool first = true;
            foreach (var c in circuit.Components)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {\"name\": ").Append(JsonStr(c.Name));
                sb.Append(", \"type\": ").Append(JsonStr(c.GetType().Name));
                sb.Append(", \"value\": ").Append(JsonStr(CompValue(c)));
                sb.Append(", \"isPot\": ").Append(c is IPotControl ? "true" : "false");
                // All scalar properties (tube model params, Model enum, R/C values, ...)
                // so the ngspice translator can rebuild the exact device.
                sb.Append(", \"params\": {");
                bool pf = true;
                foreach (var prop in c.GetType().GetProperties())
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    object val;
                    try { val = prop.GetValue(c); } catch { continue; }
                    if (val == null) continue;
                    var vt = val.GetType();
                    if (val is double || val is int || vt.IsEnum || vt.Name == "Quantity")
                    {
                        if (!pf) sb.Append(", ");
                        pf = false;
                        // Quantity params (R/C/µ-valued) via QNum for the same micro-sign reason.
                        string vs = (val is Quantity q2) ? QNum(q2) : val.ToString();
                        sb.Append(JsonStr(prop.Name)).Append(": ").Append(JsonStr(vs));
                    }
                }
                sb.Append("}");
                sb.Append(", \"terminals\": [");
                bool tf = true;
                foreach (var t in c.Terminals)
                {
                    if (!tf) sb.Append(", ");
                    tf = false;
                    sb.Append("{\"name\": ").Append(JsonStr(t.Name));
                    sb.Append(", \"node\": ").Append(JsonStr(t.ConnectedTo != null ? t.ConnectedTo.Name : null));
                    sb.Append("}");
                }
                sb.Append("]}");
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"Wrote netlist: {circuit.Components.Count()} components -> {path}");
        }
    }
}
