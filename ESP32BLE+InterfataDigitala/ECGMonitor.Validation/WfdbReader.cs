using System.Collections.Generic;
using System.Threading.Tasks;

namespace ECGMonitor.Validation
{
    public sealed class WfdbRecord
    {
        public required int SamplingFrequency { get; init; }
        public required double[] Signal { get; init; } // primul canal (lead I / MLII), valori ADC brute
        public required List<(int Sample, int Code, string? Aux)> Annotations { get; init; }
    }

    // Cititor pentru MIT-BIH Arrhythmia Database (PhysioNet) — folosit pentru validarea
    // detecției R. Primitivele binare (antet, semnal format 212, adnotări) sunt în WfdbCore.
    public static class WfdbReader
    {
        private const string BaseUrl = "https://physionet.org/files/mitdb/1.0.0/";

        // Codurile de adnotare care reprezintă efectiv o bătaie/complex QRS (tabelul
        // oficial "is_qrs" din wfdb-python, întreținut de același grup MIT-LCP/PhysioNet
        // care a creat baza de date). Codurile excluse (rhythm change, comment, noise,
        // artefact izolat, P/T-wave marker etc.) nu reprezintă o bătaie reală.
        private static readonly HashSet<int> BeatCodes = new()
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 25, 30, 31, 34, 35, 38, 41,
        };

        public static bool IsBeatCode(int code) => BeatCodes.Contains(code);

        // Bătăi ectopice/premature (cele pe care extrasistola din aplicație ar trebui
        // să le semnaleze): a=ABERR(4), V=PVC(5), F=FUSION(6), J=NPC(7), A=APC(8), S=SVPB(9).
        // Bătăile de "escape" (E, j, e, n) NU sunt premature — vin târziu, nu timpuriu —
        // deci nu fac parte din acest set.
        private static readonly HashSet<int> PrematureCodes = new() { 4, 5, 6, 7, 8, 9 };

        public static bool IsPrematureCode(int code) => PrematureCodes.Contains(code);

        public static async Task<WfdbRecord> LoadAsync(string recordName, string cacheDir)
        {
            string heaPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "hea", cacheDir);
            string datPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "dat", cacheDir);
            string atrPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "atr", cacheDir);

            var (sampFreq, nSig, nSamples) = WfdbCore.ReadHeader(heaPath);
            double[][] channels = WfdbCore.ReadSignalFormat212(datPath, nSig, nSamples);
            var annotations = WfdbCore.ReadAnnotations(atrPath);

            return new WfdbRecord
            {
                SamplingFrequency = sampFreq,
                Signal = channels[0],
                Annotations = annotations,
            };
        }
    }
}
