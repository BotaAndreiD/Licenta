using System;
using System.Collections.Generic;
using System.Linq;

namespace ECGMonitor
{
    public static class EcgAnalyzer
    {
        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        public static List<int> DetectRPeaks(List<double> times, List<double> amplitudes)
        {
            var rIndices = new List<int>();
            int n = amplitudes.Count;
            if (n < 100)
                return rIndices;

            double minAmp = amplitudes.Min();
            double range = amplitudes.Max() - minAmp;
            double median = Median(amplitudes);
            double mad = Median(amplitudes.Select(a => Math.Abs(a - median)).ToList());
            double robustSigma = mad * 1.4826;
            double rThreshold = Math.Max(median + robustSigma * 3.0, minAmp + range * 0.35);

            double avgDt = (times[n - 1] - times[0]) / Math.Max(1, n - 1);
            if (avgDt <= 0)
                return rIndices;
            int minRRSamples = Math.Max(1, (int)Math.Round(0.30 / avgDt));

            for (int i = 2; i < n - 2; i++)
                if (
                    amplitudes[i] > rThreshold
                    && amplitudes[i] > amplitudes[i - 1]
                    && amplitudes[i] > amplitudes[i - 2]
                    && amplitudes[i] > amplitudes[i + 1]
                    && amplitudes[i] > amplitudes[i + 2]
                )
                    if (rIndices.Count == 0 || i - rIndices[^1] > minRRSamples)
                        rIndices.Add(i);

            return rIndices;
        }

        public static (List<EcgEvent> events, string avgHr, string rhythmSummary) DetectEvents(
            List<double> times,
            List<double> amplitudes
        )
        {
            var events = new List<EcgEvent>();
            var rIndices = DetectRPeaks(times, amplitudes);

            if (rIndices.Count < 3)
                return (events, "--", "Date insuficiente");

            var beatTimes = rIndices.Select(i => times[i]).ToList();
            var rr = new List<double>();
            for (int i = 1; i < beatTimes.Count; i++)
                rr.Add(beatTimes[i] - beatTimes[i - 1]);

            double avgRR = rr.Average();
            double avgBpm = 60.0 / avgRR;

            DetectSustainedRuns(
                events,
                rr,
                beatTimes,
                bpm => bpm > 110,
                "Tahicardie",
                minBeats: 4
            );
            DetectSustainedRuns(
                events,
                rr,
                beatTimes,
                bpm => bpm < 50,
                "Bradicardie",
                minBeats: 4
            );

            // Comparam fiecare bataie cu media batailor RECENTE (nu cu media pe toata sesiunea),
            // ca sa nu marcam fals "extrasistole" doar pentru ca pulsul a crescut/scazut natural
            // intr-o alta parte a unei inregistrari lungi.
            const int lookback = 5;
            for (int i = 0; i < rr.Count; i++)
            {
                int start = Math.Max(0, i - lookback);
                int count = i - start;
                double localAvg = count > 0 ? rr.Skip(start).Take(count).Average() : avgRR;
                if (rr[i] < localAvg * 0.80)
                    events.Add(
                        new EcgEvent
                        {
                            Type = "Extrasistolă",
                            StartTime = beatTimes[i],
                            EndTime = beatTimes[i + 1],
                            Detail = "Bătaie prematură (RR redus)",
                        }
                    );
            }

            DetectIrregularRhythm(events, rr, beatTimes);

            const double pauseThresholdSeconds = 1.5;
            // RR-uri peste 5s nu sunt fiziologic plauzibile - de obicei sunt artefact al unui
            // gap de date (ex. reconectare BLE), nu o pauza cardiaca reala.
            const double maxPlausiblePauseSeconds = 5.0;
            for (int i = 0; i < rr.Count; i++)
                if (rr[i] > pauseThresholdSeconds && rr[i] <= maxPlausiblePauseSeconds)
                    events.Add(
                        new EcgEvent
                        {
                            Type = "Pauză",
                            StartTime = beatTimes[i],
                            EndTime = beatTimes[i + 1],
                            Detail = $"Pauză R-R: {(int)(rr[i] * 1000)} ms",
                        }
                    );

            events.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            string rhythmSummary = events.Any(e =>
                e.Type == "Tahicardie" || e.Type == "Bradicardie" || e.Type == "Ritm neregulat" || e.Type == "Pauză"
            )
                ? "Evenimente detectate — vezi lista"
                : "Ritm sinusal regulat pe toată durata";

            return (events, ((int)avgBpm).ToString(), rhythmSummary);
        }

        private static void DetectSustainedRuns(
            List<EcgEvent> events,
            List<double> rr,
            List<double> beatTimes,
            Func<double, bool> condition,
            string label,
            int minBeats
        )
        {
            int runStart = -1;
            for (int i = 0; i <= rr.Count; i++)
            {
                bool match = i < rr.Count && condition(60.0 / rr[i]);
                if (match && runStart == -1)
                    runStart = i;

                if (!match && runStart != -1)
                {
                    int runLen = i - runStart;
                    if (runLen >= minBeats)
                    {
                        var runBpms = Enumerable
                            .Range(runStart, runLen)
                            .Select(j => 60.0 / rr[j])
                            .ToList();
                        double extreme = label == "Tahicardie" ? runBpms.Max() : runBpms.Min();
                        events.Add(
                            new EcgEvent
                            {
                                Type = label,
                                StartTime = beatTimes[runStart],
                                EndTime = beatTimes[i],
                                Detail = $"Puls {(label == "Tahicardie" ? "maxim" : "minim")}: {(int)extreme} bpm, {runLen} bătăi",
                            }
                        );
                    }
                    runStart = -1;
                }
            }
        }

        private static void DetectIrregularRhythm(
            List<EcgEvent> events,
            List<double> rr,
            List<double> beatTimes
        )
        {
            const int windowBeats = 8;
            const double stdThreshold = 0.15;
            bool[] flagged = new bool[rr.Count];

            for (int i = 0; i + windowBeats <= rr.Count; i++)
            {
                var window = rr.Skip(i).Take(windowBeats).ToList();
                double mean = window.Average();
                double std = Math.Sqrt(window.Select(r => Math.Pow(r - mean, 2)).Average());
                if (std > stdThreshold)
                    for (int j = i; j < i + windowBeats; j++)
                        flagged[j] = true;
            }

            int runStart = -1;
            for (int i = 0; i <= flagged.Length; i++)
            {
                bool match = i < flagged.Length && flagged[i];
                if (match && runStart == -1)
                    runStart = i;
                if (!match && runStart != -1)
                {
                    events.Add(
                        new EcgEvent
                        {
                            Type = "Ritm neregulat",
                            StartTime = beatTimes[runStart],
                            EndTime = beatTimes[i],
                            Detail = "Variabilitate R-R ridicată",
                        }
                    );
                    runStart = -1;
                }
            }
        }
    }
}
