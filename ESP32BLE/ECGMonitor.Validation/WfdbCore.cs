using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ECGMonitor.Validation
{
    // Primitive generice de citire a formatului WFDB (PhysioNet) — partajate între
    // cititorul MIT-BIH (WfdbReader) și cel pentru QT Database (QtdbReader), ca să nu
    // se dubleze parsarea binară a antetului/semnalului/adnotărilor.
    public static class WfdbCore
    {
        private static readonly HttpClient _http = new();

        public static async Task<string> EnsureDownloadedAsync(
            string baseUrl,
            string recordName,
            string ext,
            string cacheDir
        )
        {
            Directory.CreateDirectory(cacheDir);
            string path = Path.Combine(cacheDir, $"{recordName}.{ext}");
            if (File.Exists(path))
                return path;

            byte[] data = await _http.GetByteArrayAsync($"{baseUrl}{recordName}.{ext}");
            await File.WriteAllBytesAsync(path, data);
            return path;
        }

        public static (int sampFreq, int nSig, int nSamples) ReadHeader(string heaPath)
        {
            var lines = File.ReadAllLines(heaPath).Where(l => l.Length > 0 && l[0] != '#').ToList();
            var first = lines[0].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            int nSig = int.Parse(first[1]);
            // Frecvența poate apărea ca "360" sau "250/360" (înregistrări re-eșantionate
            // ca în QT Database) — luăm doar partea înainte de '/'.
            string freqToken = first[2].Split('/')[0];
            int sampFreq = int.Parse(freqToken);
            int nSamples = first.Length > 3 ? int.Parse(first[3]) : -1;
            return (sampFreq, nSig, nSamples);
        }

        // Format "212": fiecare grup de 3 octeți codifică 2 eșantioane pe 12 biți.
        // sample[2k]   = byte[3k]   + 256*(byte[3k+1] & 0x0F)
        // sample[2k+1] = byte[3k+2] + 256*((byte[3k+1] >> 4) & 0x0F)
        // valorile > 2047 sunt negative (complement la 2 pe 12 biți).
        public static double[][] ReadSignalFormat212(string datPath, int nSig, int nSamplesHint)
        {
            byte[] raw = File.ReadAllBytes(datPath);
            int totalPairedSamples = (raw.Length / 3) * 2; // eșantioane totale, toate canalele interleaved
            int nSamplesPerChannel = nSamplesHint > 0 ? nSamplesHint : totalPairedSamples / nSig;

            var flat = new int[totalPairedSamples];
            int groups = raw.Length / 3;
            for (int g = 0; g < groups; g++)
            {
                byte b0 = raw[g * 3];
                byte b1 = raw[g * 3 + 1];
                byte b2 = raw[g * 3 + 2];

                int s0 = b0 + 256 * (b1 & 0x0F);
                int s1 = b2 + 256 * ((b1 >> 4) & 0x0F);
                if (s0 > 2047)
                    s0 -= 4096;
                if (s1 > 2047)
                    s1 -= 4096;

                flat[g * 2] = s0;
                flat[g * 2 + 1] = s1;
            }

            var channels = new double[nSig][];
            for (int c = 0; c < nSig; c++)
                channels[c] = new double[nSamplesPerChannel];

            for (int i = 0; i < nSamplesPerChannel; i++)
            for (int c = 0; c < nSig; c++)
            {
                int flatIdx = i * nSig + c;
                if (flatIdx < flat.Length)
                    channels[c][i] = flat[flatIdx];
            }

            return channels;
        }

        // Format binar de adnotări MIT (ANNOT(5)): fiecare cuvânt de 16 biți (little-endian)
        // are codul A în cei 6 biți superiori și un câmp I de 10 biți inferiori. Pentru
        // adnotările normale (0 < A <= 49), I e un delta de timp (eșantioane) față de
        // adnotația precedentă. SKIP/NUM/SUB/CHN/AUX repurposează I cu alt sens (NU e
        // delta de timp pentru ele) — tratarea greșită ar deriva progresiv poziția
        // tuturor adnotărilor ulterioare din fișier. Folosit identic pentru .atr (MIT-BIH)
        // și .q1c/.pu0 (QT Database) — același format binar, coduri diferite.
        public static List<(int Sample, int Code, string? Aux)> ReadAnnotations(string annPath)
        {
            byte[] data = File.ReadAllBytes(annPath);
            var result = new List<(int Sample, int Code, string? Aux)>();

            long time = 0;
            int pos = 0;

            ushort ReadWord()
            {
                ushort w = (ushort)(data[pos] | (data[pos + 1] << 8));
                pos += 2;
                return w;
            }

            while (pos + 1 < data.Length)
            {
                ushort word = ReadWord();
                int code = word >> 10;
                int low10 = word & 0x3FF;

                if (code == 0 && low10 == 0)
                    break; // sfârșit de fișier

                switch (code)
                {
                    case 59: // SKIP — interval mare, în următoarele 4 octeți (2 cuvinte, high apoi low)
                    {
                        ushort hi = ReadWord();
                        ushort lo = ReadWord();
                        int delta = (hi << 16) | lo;
                        time += delta;
                        break;
                    }
                    case 60: // NUM — repurposează I ca valoare "num", nu e delta de timp
                    case 61: // SUB
                    case 62: // CHN
                        break;
                    case 63: // AUX — I = lungimea în octeți a textului auxiliar care urmează
                    {
                        int len = low10;
                        string aux =
                            pos + len <= data.Length
                                ? System.Text.Encoding.ASCII.GetString(data, pos, len)
                                : "";
                        pos += len;
                        if ((len & 1) == 1)
                            pos += 1; // padding la lungime pară
                        if (result.Count > 0)
                            result[^1] = (result[^1].Sample, result[^1].Code, aux);
                        break;
                    }
                    default: // adnotație normală (0 < A <= 49): I e delta de timp real
                        time += low10;
                        result.Add(((int)time, code, null));
                        break;
                }
            }

            return result;
        }
    }
}
