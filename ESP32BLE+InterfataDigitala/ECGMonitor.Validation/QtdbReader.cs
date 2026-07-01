using System.Collections.Generic;
using System.Threading.Tasks;

namespace ECGMonitor.Validation
{
    public sealed class QtdbRecord
    {
        public required int SamplingFrequency { get; init; }
        public required double[] Signal { get; init; }
        public required List<(int Sample, int Code, string? Aux)> Annotations { get; init; }
    }

    // Cititor pentru QT Database (PhysioNet) — adnotări de delimitare a undelor
    // (P/QRS/T onset-peak-offset), verificate manual de un cardiolog (".q1c"),
    // folosite pentru a valida intervalele PR, durata QRS și QTc calculate de aplicație.
    public static class QtdbReader
    {
        private const string BaseUrl = "https://physionet.org/files/qtdb/1.0.0/";

        public static async Task<QtdbRecord> LoadAsync(string recordName, string cacheDir)
        {
            string heaPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "hea", cacheDir);
            string datPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "dat", cacheDir);
            string q1cPath = await WfdbCore.EnsureDownloadedAsync(BaseUrl, recordName, "q1c", cacheDir);

            var (sampFreq, nSig, nSamples) = WfdbCore.ReadHeader(heaPath);
            double[][] channels = WfdbCore.ReadSignalFormat212(datPath, nSig, nSamples);
            var annotations = WfdbCore.ReadAnnotations(q1cPath);

            return new QtdbRecord
            {
                SamplingFrequency = sampFreq,
                Signal = channels[0],
                Annotations = annotations,
            };
        }
    }
}
