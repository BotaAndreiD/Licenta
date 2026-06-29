using System;
using System.Collections.Generic;
using System.Linq;

namespace ECGMonitor
{
    // Algoritmul de detecție R, extras din MainWindow ca să poată fi rulat și din
    // afara aplicației WPF (ex. proiectul de validare pe MIT-BIH Arrhythmia Database),
    // garantând că validarea testează exact codul folosit live, nu o copie care poate diverge.
    // Pozițiile (în eșantioane) ale undelor Q/S/P/T pentru o bătaie, plus deviația
    // ST — folosite pentru a calcula intervalele PR, durata QRS, QTc și segmentul ST.
    public sealed class BeatWaves
    {
        public int RIndex { get; init; }
        public int? QIndex { get; init; }
        public int? SIndex { get; init; }
        public int? PIndex { get; init; }
        public int? TIndex { get; init; }
        public double? StDeviation { get; init; }
    }

    public static class ECGDetector
    {
        // Detectează Q, S, P, T pentru fiecare bătaie R, cu ferestre de căutare
        // proporționale cu RR-ul local (rezolvă bradicardia/tahicardia, unde undele
        // sunt mai depărtate/apropiate decât presupun ferestre fixe), plus deviația
        // ST (J-point la ~60ms după S, față de linia izoelectrică PR).
        public static List<BeatWaves> DetectWaveBoundaries(
            List<double> amps,
            List<int> rIndices,
            double avgDt,
            double minAmp,
            double range
        )
        {
            int n = amps.Count;
            int OffsetSamples(double seconds) => Math.Max(1, (int)Math.Round(seconds / avgDt));
            var result = new List<BeatWaves>();

            for (int beatIdx = 0; beatIdx < rIndices.Count; beatIdx++)
            {
                int ri = rIndices[beatIdx];

                double localRR;
                if (beatIdx > 0 && beatIdx < rIndices.Count - 1)
                    localRR = (rIndices[beatIdx + 1] - rIndices[beatIdx - 1]) * avgDt / 2.0;
                else if (beatIdx > 0)
                    localRR = (rIndices[beatIdx] - rIndices[beatIdx - 1]) * avgDt;
                else if (beatIdx < rIndices.Count - 1)
                    localRR = (rIndices[beatIdx + 1] - rIndices[beatIdx]) * avgDt;
                else
                    localRR = 0.80;
                localRR = Math.Clamp(localRR, 0.33, 2.0);

                int qWinStart = OffsetSamples(Math.Min(localRR * 0.15, 0.117));
                int qWinEnd = OffsetSamples(0.015);
                int sWinStart = OffsetSamples(0.015);
                int sWinEnd = OffsetSamples(Math.Min(localRR * 0.15, 0.117));
                int pWinStart = OffsetSamples(Math.Min(localRR * 0.70, 0.250));
                int pWinEnd = OffsetSamples(Math.Max(localRR * 0.25, 0.100));
                int tWinStart = OffsetSamples(0.140);
                int tWinEnd = OffsetSamples(Math.Min(localRR * 0.60, 0.500));

                int? qIndex = null,
                    sIndex = null,
                    pIndex = null,
                    tIndex = null;

                // ── Q ──────────────────────────────────────────────────────────
                int qStart = Math.Max(0, ri - qWinStart);
                int qEnd = Math.Max(0, ri - qWinEnd);
                if (qStart < qEnd)
                {
                    int qIdx = qStart;
                    for (int j = qStart; j <= qEnd; j++)
                        if (amps[j] < amps[qIdx])
                            qIdx = j;
                    qIndex = qIdx;
                }

                // ── S ──────────────────────────────────────────────────────────
                int sStart = Math.Min(n - 1, ri + sWinStart);
                int sEnd = Math.Min(n - 1, ri + sWinEnd);
                if (sStart < sEnd)
                {
                    int sIdx = sStart;
                    for (int j = sStart; j <= sEnd; j++)
                        if (amps[j] < amps[sIdx])
                            sIdx = j;
                    sIndex = sIdx;
                }

                // ── P ──────────────────────────────────────────────────────────
                int pStart = Math.Max(0, ri - pWinStart);
                int pEnd = Math.Max(0, ri - pWinEnd);
                if (pStart < pEnd)
                {
                    int pIdx = pStart;
                    for (int j = pStart; j <= pEnd; j++)
                        if (amps[j] > amps[pIdx])
                            pIdx = j;
                    if (amps[pIdx] > minAmp + range * 0.05)
                        pIndex = pIdx;
                }

                // ── T ──────────────────────────────────────────────────────────
                int tStart = Math.Min(n - 1, ri + tWinStart);
                int tEnd = Math.Min(n - 1, ri + tWinEnd);
                if (tStart < tEnd)
                {
                    int tIdx = tStart;
                    for (int j = tStart; j <= tEnd; j++)
                        if (amps[j] > amps[tIdx])
                            tIdx = j;
                    if (amps[tIdx] > minAmp + range * 0.05)
                        tIndex = tIdx;
                }

                // ── Segment ST: deviația J-point față de baseline PR ────────────
                double? stDeviation = null;
                if (sIndex.HasValue && qIndex.HasValue)
                {
                    int sIdx = sIndex.Value;
                    int qIdx = qIndex.Value;
                    int jOffset = OffsetSamples(0.060);
                    int jIdx = Math.Min(n - 1, sIdx + jOffset);
                    double jAmp = amps[jIdx];

                    double baseline;
                    if (pIndex.HasValue && pIndex.Value < qIdx)
                    {
                        int pIdx = pIndex.Value;
                        int prLen = qIdx - pIdx;
                        baseline =
                            prLen > 2
                                ? Enumerable.Range(pIdx, prLen).Select(k => amps[k]).Average()
                                : (amps[pIdx] + amps[qIdx]) / 2.0;
                    }
                    else
                    {
                        int isoIdx = Math.Max(0, qIdx - OffsetSamples(0.040));
                        baseline = amps[isoIdx];
                    }
                    stDeviation = jAmp - baseline;
                }

                result.Add(
                    new BeatWaves
                    {
                        RIndex = ri,
                        QIndex = qIndex,
                        SIndex = sIndex,
                        PIndex = pIndex,
                        TIndex = tIndex,
                        StDeviation = stDeviation,
                    }
                );
            }
            return result;
        }

        // Extrasistolă = bătaie prematură (RR scurt) confirmată fie de o pauză
        // compensatorie (RR următor anormal de lung), fie de o amplitudine R diferită
        // de cea tipică a pacientului (bătăile ectopice au de obicei o formă/amplitudine
        // diferită). Un RR scurt izolat NU e suficient — variabilitatea sinusală normală
        // (respirație) produce constant asemenea RR-uri la oameni sănătoși. Pauza
        // compensatorie singură nu prinde seriile de extrasistole consecutive (RR-ul
        // următor e tot scurt, nu o pauză) — de aceea amplitudinea e un al doilea semnal,
        // independent de timing. Folosim mediana, nu media, ca reper, ca să nu fie trasă
        // chiar de valorile extreme pe care le evaluăm.
        //
        // Notă: am testat și un al treilea semnal (template matching pe forma bătăii,
        // prin corelație Pearson) — empiric a scăzut precizia chiar la praguri foarte
        // strict calibrate, fiindcă un jitter de 1-2 eșantioane în poziția vârfului R
        // (normal, din backproiecție) distruge corelația pe panta abruptă a QRS-ului,
        // inclusiv pentru bătăi complet normale. Ar necesita aliniere sub-eșantion mult
        // mai elaborată ca să funcționeze — nu a fost păstrat.
        public static List<int> DetectExtrasystoleIndices(List<int> rIndices, List<double> amps)
        {
            var result = new List<int>();
            if (rIndices.Count < 3)
                return result;

            var rr = new List<double>();
            for (int i = 1; i < rIndices.Count; i++)
                rr.Add(rIndices[i] - rIndices[i - 1]);
            double medianRR = Median(rr);

            var beatAmps = rIndices.Select(idx => amps[idx]).ToList();
            double medianAmp = Median(beatAmps);
            double amplMad = Median(beatAmps.Select(a => Math.Abs(a - medianAmp)).ToList());

            for (int i = 0; i < rr.Count - 1; i++)
            {
                bool premature = rr[i] < medianRR * 0.78;
                bool compensatoryPause = rr[i + 1] > medianRR * 1.15;
                bool amplitudeDeviates =
                    amplMad > 0 && Math.Abs(beatAmps[i + 1] - medianAmp) > amplMad * 5.0;
                if (premature && (compensatoryPause || amplitudeDeviates))
                    result.Add(rIndices[i + 1]);
            }
            return result;
        }

        public static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        // Percentilă robustă — folosită în loc de Min()/Max() brut, ca un singur
        // artefact de mișcare/zgomot din fereastră să nu domine pragul de detecție
        // R pentru toate bătăile (cauza principală a "nu detectează R" intermitent).
        public static double Percentile(List<double> values, double pct)
        {
            var sorted = values.OrderBy(v => v).ToList();
            double rank = pct / 100.0 * (sorted.Count - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi)
                return sorted[lo];
            double frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        // Pan-Tompkins preprocessing (1985) — preprocesare standard pentru detecție R robustă.
        // Derivată în 5 puncte → pătrat → integrare pe fereastră mobilă (150ms).
        // Rezultatul e un envelope pozitiv cu vârfuri clare la complexele QRS,
        // independent de amplitudinea absolută a semnalului.
        public static List<double> PanTompkinsEnvelope(List<double> signal, double avgDt)
        {
            int n = signal.Count;

            // 1. Derivată în 5 puncte — accentuează pantele abrupte QRS
            var deriv = new double[n];
            for (int i = 2; i < n - 2; i++)
                deriv[i] =
                    (signal[i + 2] + 2 * signal[i + 1] - 2 * signal[i - 1] - signal[i - 2]) / 8.0;

            // 2. Ridicare la pătrat — toate valorile pozitive, vârfurile mari domină
            var squared = new double[n];
            for (int i = 0; i < n; i++)
                squared[i] = deriv[i] * deriv[i];

            // 3. Integrare pe fereastră mobilă ~150ms — netezire, formează envelope
            int winSize = Math.Max(2, (int)Math.Round(0.150 / avgDt));
            var result = new List<double>(n);
            double runSum = 0;
            var winQueue = new Queue<double>(winSize + 1);
            for (int i = 0; i < n; i++)
            {
                runSum += squared[i];
                winQueue.Enqueue(squared[i]);
                if (winQueue.Count > winSize)
                    runSum -= winQueue.Dequeue();
                result.Add(runSum / winQueue.Count);
            }
            return result;
        }

        // Detecție R adaptivă (Pan & Tompkins, 1985) — SPKI/NPKI sunt nivele care
        // evoluează bătaie-cu-bătaie, nu un prag static calculat o singură dată din
        // toată fereastra. Asta rezolvă exact problema unde 2-3 bătăi neobișnuit de
        // înalte dintr-o fereastră ridicau pragul global și excludeau bătăi normale,
        // dar mai mici, aflate în apropiere (variație naturală de amplitudine R).
        public static List<int> DetectRIndicesAdaptive(
            List<double> amps,
            List<double> envelope,
            double avgDt,
            double minAmp,
            double range
        )
        {
            int n = envelope.Count;
            int OffsetSamples(double seconds) => Math.Max(1, (int)Math.Round(seconds / avgDt));

            int refractorySamples = OffsetSamples(0.20); // refractar fiziologic absolut
            int backprojectWin = OffsetSamples(0.060);
            int tWaveWindowSamples = OffsetSamples(0.36); // fereastră discriminare T-wave

            // ── Inițializare SPKI/NPKI dintr-o fereastră scurtă de "încălzire" ──
            int warmup = Math.Min(n, OffsetSamples(1.0));
            var warmupSlice = envelope.Take(warmup).ToList();
            double warmupMedian = Median(warmupSlice);
            double spki = Math.Max(Percentile(warmupSlice, 90), warmupMedian * 2.0);
            double npki = warmupMedian;
            if (spki <= npki)
                spki = npki + 1e-9;

            double rrAverage = OffsetSamples(0.8) * avgDt; // estimare inițială ~75 bpm
            var recentRR = new List<double>();

            var peakIndices = new List<int>();
            int lastR = -1;
            double lastQrsPeakVal = spki;

            int i = 2;
            while (i < n - 2)
            {
                bool isPeak =
                    envelope[i] > envelope[i - 1]
                    && envelope[i] > envelope[i - 2]
                    && envelope[i] > envelope[i + 1]
                    && envelope[i] > envelope[i + 2];

                if (!isPeak)
                {
                    i++;
                    continue;
                }

                double threshold1 = npki + 0.25 * (spki - npki);
                double peakVal = envelope[i];
                bool inRefractory = lastR >= 0 && i - lastR <= refractorySamples;

                if (peakVal > threshold1 && !inRefractory)
                {
                    // Discriminare T-wave: vârf apropiat de R-ul anterior și mult mai mic
                    // decât el e probabil T, nu un nou QRS.
                    bool looksLikeTWave =
                        lastR >= 0
                        && i - lastR < tWaveWindowSamples
                        && peakVal < lastQrsPeakVal * 0.5;

                    if (!looksLikeTWave)
                    {
                        spki = 0.125 * peakVal + 0.875 * spki;
                        lastQrsPeakVal = peakVal;
                        if (lastR >= 0)
                        {
                            recentRR.Add((i - lastR) * avgDt);
                            if (recentRR.Count > 8)
                                recentRR.RemoveAt(0);
                            rrAverage = recentRR.Average();
                        }
                        lastR = i;
                        peakIndices.Add(i);
                        i += Math.Max(1, refractorySamples / 2);
                        continue;
                    }
                }

                npki = 0.125 * peakVal + 0.875 * npki;

                // ── Search-back: dacă a trecut prea mult față de RR-ul mediu recent,
                // un vârf real a fost probabil sub prag — recăutăm cu jumătate din prag ──
                if (lastR >= 0 && rrAverage > 0)
                {
                    int missedLimitSamples = (int)Math.Round(1.66 * rrAverage / avgDt);
                    if (i - lastR > missedLimitSamples)
                    {
                        double threshold2 = 0.5 * threshold1;
                        int searchStart = lastR + refractorySamples;
                        int bestIdx = -1;
                        double bestVal = threshold2;
                        for (int j = searchStart; j <= i; j++)
                        {
                            if (j < 2 || j >= n - 2)
                                continue;
                            bool isLocalPeak =
                                envelope[j] > envelope[j - 1]
                                && envelope[j] > envelope[j - 2]
                                && envelope[j] > envelope[j + 1]
                                && envelope[j] > envelope[j + 2];
                            if (isLocalPeak && envelope[j] > bestVal)
                            {
                                bestVal = envelope[j];
                                bestIdx = j;
                            }
                        }
                        if (bestIdx >= 0)
                        {
                            spki = 0.25 * bestVal + 0.75 * spki;
                            recentRR.Add((bestIdx - lastR) * avgDt);
                            if (recentRR.Count > 8)
                                recentRR.RemoveAt(0);
                            rrAverage = recentRR.Average();
                            lastR = bestIdx;
                            lastQrsPeakVal = bestVal;
                            peakIndices.Add(bestIdx);
                        }
                    }
                }

                i++;
            }

            peakIndices.Sort();

            // ── Backproiecție: maximul semnalului brut în ±60ms față de vârful envelope ──
            var actualIndices = new List<int>();
            foreach (int idx in peakIndices)
            {
                int lo = Math.Max(0, idx - backprojectWin);
                int hi = Math.Min(amps.Count - 1, idx + backprojectWin);
                int actualR = lo;
                for (int j = lo + 1; j <= hi; j++)
                    if (amps[j] > amps[actualR])
                        actualR = j;
                if (
                    amps[actualR] > minAmp + range * 0.15
                    && (actualIndices.Count == 0 || actualR - actualIndices[^1] > refractorySamples)
                )
                    actualIndices.Add(actualR);
            }
            return actualIndices;
        }
    }
}
