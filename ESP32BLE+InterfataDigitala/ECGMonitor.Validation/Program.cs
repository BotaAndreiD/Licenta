using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ECGMonitor;
using ECGMonitor.Validation;

bool skipMitbih = args.Length > 0 && args[0] == "--qtdb-only";
bool skipQtdb = args.Length > 0 && args[0] == "--mitbih-only";

string resultsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results");
Directory.CreateDirectory(resultsDir);

if (!skipMitbih)
{
    // ── Validare a detectorului R pe MIT-BIH Arrhythmia Database (PhysioNet) ──────
    // Rulează exact algoritmul folosit live în aplicație (ECGDetector, din proiectul
    // ECGMonitor) pe înregistrări reale adnotate manual de cardiologi, și calculează
    // Sensibilitate (Se) și Predictivitate pozitivă (+P) — metricile standard folosite
    // în literatura de detecție QRS (Pan & Tompkins 1985, AAMI EC57).
    //
    // Set de 44 de înregistrări fără stimulator cardiac (paced) — split DS1/DS2
    // folosit pe scară largă în papers de detecție QRS pentru benchmarking.
    string[] ds1 =
    {
        "101", "106", "108", "109", "112", "114", "115", "116", "118", "119", "122",
        "124", "201", "203", "205", "207", "208", "209", "215", "220", "223", "230",
    };
    string[] ds2 =
    {
        "100", "103", "105", "111", "113", "117", "121", "123", "200", "202", "210",
        "212", "213", "214", "219", "221", "222", "228", "231", "232", "233", "234",
    };
    string[] records = ds1.Concat(ds2).ToArray();
    if (args.Length > 0 && args[0] == "--quick")
        records = new[] { "100" };
    if (args.Length > 0 && args[0] == "--only")
        records = args.Skip(1).ToArray();

    double ToleranceSeconds = 0.10; // fereastră de potrivire standard (~100ms)
    if (
        args.Length > 0
        && args[0] == "--tol"
        && args.Length > 1
        && double.TryParse(args[1], out double tolOverride)
    )
    {
        ToleranceSeconds = tolOverride;
        records = args.Skip(2).ToArray();
    }

    string cacheDir = Path.Combine(AppContext.BaseDirectory, "mitdb-cache");

    var rows = new List<RecordResult>();
    var ectopicRows = new List<RecordResult>();
    var vebRows = new List<RecordResult>();
    var svpRows = new List<RecordResult>();
    long totalTP = 0,
        totalFN = 0,
        totalFP = 0;
    long ectTotalTP = 0,
        ectTotalFN = 0,
        ectTotalFP = 0;
    long vebTotalTP = 0,
        vebTotalFN = 0,
        vebTotalFP = 0;
    long svpTotalTP = 0,
        svpTotalFN = 0,
        svpTotalFP = 0;

    foreach (string rec in records)
    {
        Console.Write($"[{rec}] descarc/încarc... ");
        WfdbRecord wf;
        try
        {
            wf = await WfdbReader.LoadAsync(rec, cacheDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EȘUAT ({ex.Message})");
            continue;
        }

        double avgDt = 1.0 / wf.SamplingFrequency;
        var amps = wf.Signal.ToList();
        double minAmp = ECGDetector.Percentile(amps, 2);
        double maxAmp = ECGDetector.Percentile(amps, 98);
        double range = maxAmp - minAmp;

        var envelope = ECGDetector.PanTompkinsEnvelope(amps, avgDt);
        List<int> detected = ECGDetector.DetectRIndicesAdaptive(
            amps,
            envelope,
            avgDt,
            minAmp,
            range
        );

        List<int> truth = wf
            .Annotations.Where(a => WfdbReader.IsBeatCode(a.Code))
            .Select(a => a.Sample)
            .OrderBy(s => s)
            .ToList();

        int toleranceSamples = (int)Math.Round(ToleranceSeconds * wf.SamplingFrequency);
        var (tp, fn, fp) = MatchBeats(truth, detected, toleranceSamples);

        double se = tp + fn > 0 ? 100.0 * tp / (tp + fn) : 0;
        double pp = tp + fp > 0 ? 100.0 * tp / (tp + fp) : 0;

        rows.Add(new RecordResult(rec, truth.Count, detected.Count, tp, fn, fp, se, pp));
        totalTP += tp;
        totalFN += fn;
        totalFP += fp;

        Console.WriteLine(
            $"bătăi reale={truth.Count,5} detectate={detected.Count,5} TP={tp,5} FN={fn,4} FP={fp,4}  Se={se,6:F2}%  +P={pp,6:F2}%"
        );

        // ── Extrasistole: comparăm bătăile semnalate de euristica aplicației (vezi cele
        // trei căi în ECGDetector.DetectExtrasystoleIndices) cu bătăile ectopice/premature
        // adnotate de cardiologi ──────────────────────────────────────────────────────
        List<int> ectTruth = wf
            .Annotations.Where(a => WfdbReader.IsPrematureCode(a.Code))
            .Select(a => a.Sample)
            .OrderBy(s => s)
            .ToList();
        var ectBeatWaves = ECGDetector.DetectWaveBoundaries(amps, detected, avgDt, minAmp, range);
        List<int> ectDetected = ECGDetector.DetectExtrasystoleIndices(
            detected,
            amps,
            ectBeatWaves,
            avgDt
        );
        var (ectTp, ectFn, ectFp) = MatchBeats(ectTruth, ectDetected, toleranceSamples);
        double ectSe = ectTp + ectFn > 0 ? 100.0 * ectTp / (ectTp + ectFn) : 0;
        double ectPp = ectTp + ectFp > 0 ? 100.0 * ectTp / (ectTp + ectFp) : 0;
        ectopicRows.Add(
            new RecordResult(
                rec,
                ectTruth.Count,
                ectDetected.Count,
                ectTp,
                ectFn,
                ectFp,
                ectSe,
                ectPp
            )
        );
        ectTotalTP += ectTp;
        ectTotalFN += ectFn;
        ectTotalFP += ectFp;

        // ── Subset ventricular (V/aberant/fuziune, cod 4/5/6) — origine ventriculară,
        // deci QRS lat, exact ce detectorul poate recunoaște prin lățime. Bătăile
        // supraventriculare (J/A/S) au QRS îngust și nu au un semnal echivalent
        // în aplicație (ar necesita analiză de morfologie P), deci le separăm ca
        // să vedem plafonul real al abordării curente. ──────────────────────────
        List<int> vebTruth = wf
            .Annotations.Where(a => WfdbReader.IsVentricularPrematureCode(a.Code))
            .Select(a => a.Sample)
            .OrderBy(s => s)
            .ToList();
        var (vebTp, vebFn, vebFp) = MatchBeats(vebTruth, ectDetected, toleranceSamples);
        double vebSe = vebTp + vebFn > 0 ? 100.0 * vebTp / (vebTp + vebFn) : 0;
        double vebPp = vebTp + vebFp > 0 ? 100.0 * vebTp / (vebTp + vebFp) : 0;
        vebRows.Add(
            new RecordResult(rec, vebTruth.Count, ectDetected.Count, vebTp, vebFn, vebFp, vebSe, vebPp)
        );
        vebTotalTP += vebTp;
        vebTotalFN += vebFn;
        vebTotalFP += vebFp;

        // ── Subset supraventricular (J/A/S, cod 7/8/9) — origine atrială/nodală,
        // QRS îngust; calea C (premature+QRS îngust+pauză incompletă+PR scurt) e
        // semnalul specific pentru acest subset. ────────────────────────────────
        List<int> svpTruth = wf
            .Annotations.Where(a => WfdbReader.IsSupraventricularPrematureCode(a.Code))
            .Select(a => a.Sample)
            .OrderBy(s => s)
            .ToList();
        var (svpTp, svpFn, svpFp) = MatchBeats(svpTruth, ectDetected, toleranceSamples);
        double svpSe = svpTp + svpFn > 0 ? 100.0 * svpTp / (svpTp + svpFn) : 0;
        double svpPp = svpTp + svpFp > 0 ? 100.0 * svpTp / (svpTp + svpFp) : 0;
        svpRows.Add(
            new RecordResult(rec, svpTruth.Count, ectDetected.Count, svpTp, svpFn, svpFp, svpSe, svpPp)
        );
        svpTotalTP += svpTp;
        svpTotalFN += svpFn;
        svpTotalFP += svpFp;
    }

    double totalSe = totalTP + totalFN > 0 ? 100.0 * totalTP / (totalTP + totalFN) : 0;
    double totalPp = totalTP + totalFP > 0 ? 100.0 * totalTP / (totalTP + totalFP) : 0;

    Console.WriteLine();
    Console.WriteLine(
        $"TOTAL R ({rows.Count} înregistrări): TP={totalTP} FN={totalFN} FP={totalFP}  Se={totalSe:F2}%  +P={totalPp:F2}%"
    );

    double ectTotalSe =
        ectTotalTP + ectTotalFN > 0 ? 100.0 * ectTotalTP / (ectTotalTP + ectTotalFN) : 0;
    double ectTotalPp =
        ectTotalTP + ectTotalFP > 0 ? 100.0 * ectTotalTP / (ectTotalTP + ectTotalFP) : 0;
    Console.WriteLine(
        $"TOTAL extrasistole ({ectopicRows.Count} înregistrări): TP={ectTotalTP} FN={ectTotalFN} FP={ectTotalFP}  Se={ectTotalSe:F2}%  +P={ectTotalPp:F2}%"
    );

    WriteCsv(Path.Combine(resultsDir, "mitbih_validation.csv"), rows, totalTP, totalFN, totalFP, totalSe, totalPp);
    WriteMarkdown(
        Path.Combine(resultsDir, "mitbih_validation.md"),
        rows,
        totalTP,
        totalFN,
        totalFP,
        totalSe,
        totalPp,
        ToleranceSeconds
    );
    WriteCsv(
        Path.Combine(resultsDir, "mitbih_extrasystole_validation.csv"),
        ectopicRows,
        ectTotalTP,
        ectTotalFN,
        ectTotalFP,
        ectTotalSe,
        ectTotalPp
    );
    WriteMarkdown(
        Path.Combine(resultsDir, "mitbih_extrasystole_validation.md"),
        ectopicRows,
        ectTotalTP,
        ectTotalFN,
        ectTotalFP,
        ectTotalSe,
        ectTotalPp,
        ToleranceSeconds,
        title:
            "Validare detecție extrasistole (bătăi premature/ectopice) — MIT-BIH Arrhythmia Database"
    );

    double vebTotalSe =
        vebTotalTP + vebTotalFN > 0 ? 100.0 * vebTotalTP / (vebTotalTP + vebTotalFN) : 0;
    double vebTotalPp =
        vebTotalTP + vebTotalFP > 0 ? 100.0 * vebTotalTP / (vebTotalTP + vebTotalFP) : 0;
    Console.WriteLine(
        $"TOTAL extrasistole ventriculare ({vebRows.Count} înregistrări): TP={vebTotalTP} FN={vebTotalFN} FP={vebTotalFP}  Se={vebTotalSe:F2}%  +P={vebTotalPp:F2}%"
    );
    WriteCsv(
        Path.Combine(resultsDir, "mitbih_veb_validation.csv"),
        vebRows,
        vebTotalTP,
        vebTotalFN,
        vebTotalFP,
        vebTotalSe,
        vebTotalPp
    );
    WriteMarkdown(
        Path.Combine(resultsDir, "mitbih_veb_validation.md"),
        vebRows,
        vebTotalTP,
        vebTotalFN,
        vebTotalFP,
        vebTotalSe,
        vebTotalPp,
        ToleranceSeconds,
        title:
            "Validare detecție extrasistole VENTRICULARE (V/aberant/fuziune) — MIT-BIH Arrhythmia Database. "
            + "NOTĂ: +P e o limită inferioară — o detecție care corespunde corect unei bătăi "
            + "supraventriculare (J/A/S, cod 7/8/9) apare aici drept FP, deși e o detecție reală "
            + "a algoritmului, doar în afara acestui subset ventricular."
    );

    double svpTotalSe =
        svpTotalTP + svpTotalFN > 0 ? 100.0 * svpTotalTP / (svpTotalTP + svpTotalFN) : 0;
    double svpTotalPp =
        svpTotalTP + svpTotalFP > 0 ? 100.0 * svpTotalTP / (svpTotalTP + svpTotalFP) : 0;
    Console.WriteLine(
        $"TOTAL extrasistole supraventriculare ({svpRows.Count} înregistrări): TP={svpTotalTP} FN={svpTotalFN} FP={svpTotalFP}  Se={svpTotalSe:F2}%  +P={svpTotalPp:F2}%"
    );
    WriteCsv(
        Path.Combine(resultsDir, "mitbih_svp_validation.csv"),
        svpRows,
        svpTotalTP,
        svpTotalFN,
        svpTotalFP,
        svpTotalSe,
        svpTotalPp
    );
    WriteMarkdown(
        Path.Combine(resultsDir, "mitbih_svp_validation.md"),
        svpRows,
        svpTotalTP,
        svpTotalFN,
        svpTotalFP,
        svpTotalSe,
        svpTotalPp,
        ToleranceSeconds,
        title:
            "Validare detecție extrasistole SUPRAVENTRICULARE (nodal/atrial) — MIT-BIH Arrhythmia Database. "
            + "NOTĂ: +P e o limită inferioară — o detecție care corespunde corect unei bătăi "
            + "ventriculare (V/aberant/fuziune, cod 4/5/6) apare aici drept FP."
    );

    Console.WriteLine();
    Console.WriteLine($"Rezultate MIT-BIH scrise în: {Path.GetFullPath(resultsDir)}");
}

if (!skipQtdb)
{
    await RunQtdbValidation(args, resultsDir);
}

// ── Potrivire bătăi reale ↔ detectate (greedy, ambele liste sortate) ──────────
static (int tp, int fn, int fp) MatchBeats(List<int> truth, List<int> detected, int tolerance)
{
    int tp = 0;
    var usedDetected = new bool[detected.Count];

    foreach (int t in truth)
    {
        // Caută cea mai apropiată bătaie detectată, neutilizată, în fereastra de toleranță
        int bestIdx = -1;
        int bestDist = int.MaxValue;
        int lo = LowerBound(detected, t - tolerance);
        for (int k = lo; k < detected.Count && detected[k] <= t + tolerance; k++)
        {
            if (usedDetected[k])
                continue;
            int dist = Math.Abs(detected[k] - t);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = k;
            }
        }
        if (bestIdx >= 0)
        {
            usedDetected[bestIdx] = true;
            tp++;
        }
    }

    int fn = truth.Count - tp;
    int fp = detected.Count - tp;
    return (tp, fn, fp);
}

static int LowerBound(List<int> sorted, int value)
{
    int lo = 0,
        hi = sorted.Count;
    while (lo < hi)
    {
        int mid = (lo + hi) / 2;
        if (sorted[mid] < value)
            lo = mid + 1;
        else
            hi = mid;
    }
    return lo;
}

static void WriteCsv(
    string path,
    List<RecordResult> rows,
    long totalTP,
    long totalFN,
    long totalFP,
    double totalSe,
    double totalPp
)
{
    using var w = new StreamWriter(path);
    w.WriteLine("record,beats_truth,beats_detected,TP,FN,FP,Se_%,PP_%");
    foreach (var r in rows)
        w.WriteLine(
            $"{r.Record},{r.TruthCount},{r.DetectedCount},{r.TP},{r.FN},{r.FP},{r.Se:F2},{r.Pp:F2}"
        );
    w.WriteLine($"TOTAL,,,{totalTP},{totalFN},{totalFP},{totalSe:F2},{totalPp:F2}");
}

static void WriteMarkdown(
    string path,
    List<RecordResult> rows,
    long totalTP,
    long totalFN,
    long totalFP,
    double totalSe,
    double totalPp,
    double toleranceSeconds,
    string title = "Validare detector R — MIT-BIH Arrhythmia Database"
)
{
    using var w = new StreamWriter(path);
    w.WriteLine($"{title} ({rows.Count} înregistrări, fereastră de potrivire ±{toleranceSeconds * 1000:F0} ms)");
    w.WriteLine();
    w.WriteLine("| Înregistrare | Bătăi reale | Detectate | TP | FN | FP | Se (%) | +P (%) |");
    w.WriteLine("|---|---|---|---|---|---|---|---|");
    foreach (var r in rows)
        w.WriteLine(
            $"| {r.Record} | {r.TruthCount} | {r.DetectedCount} | {r.TP} | {r.FN} | {r.FP} | {r.Se:F2} | {r.Pp:F2} |"
        );
    w.WriteLine(
        $"| **TOTAL** | | | **{totalTP}** | **{totalFN}** | **{totalFP}** | **{totalSe:F2}** | **{totalPp:F2}** |"
    );
}

// ── Validare intervale PR / durată QRS / QT pe QT Database (PhysioNet) ────────
// QT Database are adnotări de delimitare a undelor (P/QRS/T onset-peak-offset),
// verificate manual de un cardiolog (".q1c") — exact ce-i trebuie pentru a valida
// intervalele PR/QRS/QT calculate de aplicație, nu doar poziția bătăii (ca la MIT-BIH).
static async Task RunQtdbValidation(string[] args, string resultsDir)
{
    string[] qtdbRecords =
    {
        "sel100", "sel102", "sel103", "sel104", "sel114", "sel116", "sel117", "sel123",
        "sel14046", "sel14157", "sel14172", "sel15814", "sel16265", "sel16272", "sel16273",
        "sel16420", "sel16483", "sel16539", "sel16773", "sel16786", "sel16795", "sel17152",
        "sel17453", "sel213", "sel221", "sel223", "sel230", "sel231", "sel232", "sel233",
        "sel30", "sel301", "sel302", "sel306", "sel307", "sel308", "sel31", "sel310", "sel32",
        "sel33", "sel34", "sel35", "sel36", "sel37", "sel38", "sel39", "sel40", "sel41",
        "sel42", "sel43", "sel44", "sel45", "sel46", "sel47", "sel48", "sel49", "sel50",
        "sel51", "sel52", "sel803", "sel808", "sel811", "sel820", "sel821", "sel840",
        "sel847", "sel853", "sel871", "sel872", "sel873", "sel883", "sel891", "sele0104",
        "sele0106", "sele0107", "sele0110", "sele0111", "sele0112", "sele0114", "sele0116",
        "sele0121", "sele0122", "sele0124", "sele0126", "sele0129", "sele0133", "sele0136",
        "sele0166", "sele0170", "sele0203", "sele0210", "sele0211", "sele0303", "sele0405",
        "sele0406", "sele0409", "sele0411", "sele0509", "sele0603", "sele0604", "sele0606",
        "sele0607", "sele0609", "sele0612", "sele0704",
    };
    if (args.Length > 0 && args[0] == "--qtdb-only" && args.Length > 1)
        qtdbRecords = args.Skip(1).ToArray();

    const double ToleranceSeconds = 0.05; // 50ms — bătăile reale sunt adnotate la nivel de undă
    string qtCacheDir = Path.Combine(AppContext.BaseDirectory, "qtdb-cache");

    var qtRows = new List<QtRecordResult>();
    var allPr = new List<double>();
    var allQrs = new List<double>();
    var allQt = new List<double>();

    Console.WriteLine();
    Console.WriteLine("── Validare PR / QRS / QT — QT Database (PhysioNet) ──");

    foreach (string rec in qtdbRecords)
    {
        Console.Write($"[{rec}] descarc/încarc... ");
        QtdbRecord qt;
        try
        {
            qt = await QtdbReader.LoadAsync(rec, qtCacheDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EȘUAT ({ex.Message})");
            continue;
        }

        double avgDt = 1.0 / qt.SamplingFrequency;
        var amps = qt.Signal.ToList();
        double minAmp = ECGDetector.Percentile(amps, 2);
        double maxAmp = ECGDetector.Percentile(amps, 98);
        double range = maxAmp - minAmp;

        var envelope = ECGDetector.PanTompkinsEnvelope(amps, avgDt);
        List<int> detected = ECGDetector.DetectRIndicesAdaptive(
            amps,
            envelope,
            avgDt,
            minAmp,
            range
        );
        var beatWaves = ECGDetector.DetectWaveBoundaries(amps, detected, avgDt, minAmp, range);

        var truthBeats = ExtractTruthBeats(qt.Annotations);
        int toleranceSamples = (int)Math.Round(ToleranceSeconds * qt.SamplingFrequency);
        double msPerSample = 1000.0 / qt.SamplingFrequency;

        var prErrors = new List<double>();
        var qrsErrors = new List<double>();
        var qtErrors = new List<double>();
        int matchedBeats = 0;

        foreach (var tb in truthBeats)
        {
            // bătaia detectată de aplicație cea mai apropiată de QRS-onset-ul real
            int bestIdx = -1,
                bestDist = int.MaxValue;
            for (int k = 0; k < detected.Count; k++)
            {
                int d = Math.Abs(detected[k] - tb.QrsOn);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = k;
                }
            }
            if (bestIdx < 0 || bestDist > toleranceSamples)
                continue;
            matchedBeats++;

            var bw = beatWaves[bestIdx];

            if (tb.POn.HasValue && bw.PIndex.HasValue && bw.QIndex.HasValue)
            {
                double prTruth = (tb.QrsOn - tb.POn.Value) * msPerSample;
                double prApp = (bw.QIndex.Value - bw.PIndex.Value) * msPerSample;
                prErrors.Add(prApp - prTruth);
            }
            if (bw.QIndex.HasValue && bw.SIndex.HasValue)
            {
                double qrsTruth = (tb.QrsOff - tb.QrsOn) * msPerSample;
                double qrsApp = (bw.SIndex.Value - bw.QIndex.Value) * msPerSample;
                qrsErrors.Add(qrsApp - qrsTruth);
            }
            if (bw.QIndex.HasValue && bw.TIndex.HasValue)
            {
                double qtTruth = (tb.TOff - tb.QrsOn) * msPerSample;
                double qtApp = (bw.TIndex.Value - bw.QIndex.Value) * msPerSample;
                qtErrors.Add(qtApp - qtTruth);
            }
        }

        allPr.AddRange(prErrors);
        allQrs.AddRange(qrsErrors);
        allQt.AddRange(qtErrors);

        qtRows.Add(
            new QtRecordResult(
                rec,
                truthBeats.Count,
                matchedBeats,
                prErrors.Count,
                Mean(prErrors),
                StdDev(prErrors),
                qrsErrors.Count,
                Mean(qrsErrors),
                StdDev(qrsErrors),
                qtErrors.Count,
                Mean(qtErrors),
                StdDev(qtErrors)
            )
        );

        Console.WriteLine(
            $"bătăi adnotate={truthBeats.Count,3} găsite={matchedBeats,3}  PR n={prErrors.Count,3} eroare={Mean(prErrors),6:F1}±{StdDev(prErrors),5:F1}ms  "
                + $"QRS n={qrsErrors.Count,3} eroare={Mean(qrsErrors),6:F1}±{StdDev(qrsErrors),5:F1}ms  "
                + $"QT n={qtErrors.Count,3} eroare={Mean(qtErrors),6:F1}±{StdDev(qtErrors),5:F1}ms"
        );
    }

    Console.WriteLine();
    Console.WriteLine(
        $"TOTAL PR:  n={allPr.Count,4}  eroare medie={Mean(allPr):F1}ms  |eroare| medie={MeanAbs(allPr):F1}ms  std={StdDev(allPr):F1}ms"
    );
    Console.WriteLine(
        $"TOTAL QRS: n={allQrs.Count,4}  eroare medie={Mean(allQrs):F1}ms  |eroare| medie={MeanAbs(allQrs):F1}ms  std={StdDev(allQrs):F1}ms"
    );
    Console.WriteLine(
        $"TOTAL QT:  n={allQt.Count,4}  eroare medie={Mean(allQt):F1}ms  |eroare| medie={MeanAbs(allQt):F1}ms  std={StdDev(allQt):F1}ms"
    );

    WriteQtCsv(Path.Combine(resultsDir, "qtdb_validation.csv"), qtRows, allPr, allQrs, allQt);
    WriteQtMarkdown(Path.Combine(resultsDir, "qtdb_validation.md"), qtRows, allPr, allQrs, allQt);

    Console.WriteLine();
    Console.WriteLine($"Rezultate QT Database scrise în: {Path.GetFullPath(resultsDir)}");
}

// ── Extrage bătăile adnotate (QRS obligatoriu, P opțional) din secvența q1c ────
// Secvența standard per bătaie e: ["(" "p" ")"] "(" "N" ")" "t" ")" — onset/peak/offset
// P (opțional — unele înregistrări nu au P adnotat deloc, ex. semnal prea zgomotos),
// apoi onset/peak/offset QRS (mereu prezent), apoi peak/offset T (T-onset nu e adnotat
// separat, se presupune egal cu QRS-offset). Ancorăm pe "(" urmat de "N" (QRS-onset),
// ca să nu pierdem bătăile fără P — P se caută opțional, exact înaintea ancorei.
static List<TruthBeat> ExtractTruthBeats(List<(int Sample, int Code, string? Aux)> ann)
{
    var beats = new List<TruthBeat>();
    int i = 0;
    while (i < ann.Count - 1)
    {
        if (ann[i].Code == 39 && ann[i + 1].Code == 1) // "(" urmat de "N" = QRS onset
        {
            int qrsOn = ann[i].Sample;

            int k = i + 1;
            while (k < ann.Count && ann[k].Code != 40)
                k++;
            if (k >= ann.Count)
            {
                i++;
                continue;
            }
            int qrsOff = ann[k].Sample;

            int m = k + 1;
            while (m < ann.Count && ann[m].Code != 27)
                m++;
            if (m >= ann.Count)
            {
                i = k;
                continue;
            }

            int p = m + 1;
            while (p < ann.Count && ann[p].Code != 40)
                p++;
            if (p >= ann.Count)
            {
                i = m;
                continue;
            }
            int tOff = ann[p].Sample;

            // P opțional: caută exact secvența "(" "p" ")" imediat înaintea QRS-onset-ului
            int? pOn = null;
            if (i >= 3 && ann[i - 3].Code == 39 && ann[i - 2].Code == 24 && ann[i - 1].Code == 40)
                pOn = ann[i - 3].Sample;

            beats.Add(new TruthBeat(pOn, qrsOn, qrsOff, tOff));
            i = p + 1;
        }
        else
        {
            i++;
        }
    }
    return beats;
}

static double Mean(List<double> values) => values.Count > 0 ? values.Average() : 0;

static double MeanAbs(List<double> values) => values.Count > 0 ? values.Select(Math.Abs).Average() : 0;

static double StdDev(List<double> values)
{
    if (values.Count < 2)
        return 0;
    double mean = values.Average();
    return Math.Sqrt(values.Select(v => (v - mean) * (v - mean)).Average());
}

static void WriteQtCsv(
    string path,
    List<QtRecordResult> rows,
    List<double> allPr,
    List<double> allQrs,
    List<double> allQt
)
{
    using var w = new StreamWriter(path);
    w.WriteLine(
        "record,beats_truth,beats_matched,PR_n,PR_mean_ms,PR_std_ms,QRS_n,QRS_mean_ms,QRS_std_ms,QT_n,QT_mean_ms,QT_std_ms"
    );
    foreach (var r in rows)
        w.WriteLine(
            $"{r.Record},{r.TruthCount},{r.MatchedCount},{r.PrN},{r.PrMean:F1},{r.PrStd:F1},{r.QrsN},{r.QrsMean:F1},{r.QrsStd:F1},{r.QtN},{r.QtMean:F1},{r.QtStd:F1}"
        );
    w.WriteLine(
        $"TOTAL,,{allPr.Count},{Mean(allPr):F1},{StdDev(allPr):F1},{allQrs.Count},{Mean(allQrs):F1},{StdDev(allQrs):F1},{allQt.Count},{Mean(allQt):F1},{StdDev(allQt):F1}"
    );
}

static void WriteQtMarkdown(
    string path,
    List<QtRecordResult> rows,
    List<double> allPr,
    List<double> allQrs,
    List<double> allQt
)
{
    using var w = new StreamWriter(path);
    w.WriteLine(
        $"Validare intervale PR / QRS / QT — QT Database ({rows.Count} înregistrări, eroare = valoare aplicație − valoare cardiolog, ms)"
    );
    w.WriteLine();
    w.WriteLine(
        "| Înregistrare | Bătăi adnotate | Bătăi găsite | PR n | PR eroare (ms) | QRS n | QRS eroare (ms) | QT n | QT eroare (ms) |"
    );
    w.WriteLine("|---|---|---|---|---|---|---|---|---|");
    foreach (var r in rows)
        w.WriteLine(
            $"| {r.Record} | {r.TruthCount} | {r.MatchedCount} | {r.PrN} | {r.PrMean:F1} ± {r.PrStd:F1} | {r.QrsN} | {r.QrsMean:F1} ± {r.QrsStd:F1} | {r.QtN} | {r.QtMean:F1} ± {r.QtStd:F1} |"
        );
    w.WriteLine(
        $"| **TOTAL** | | **{allPr.Count}** | **{Mean(allPr):F1} ± {StdDev(allPr):F1}** | **{allQrs.Count}** | **{Mean(allQrs):F1} ± {StdDev(allQrs):F1}** | **{allQt.Count}** | **{Mean(allQt):F1} ± {StdDev(allQt):F1}** |"
    );
}

record TruthBeat(int? POn, int QrsOn, int QrsOff, int TOff);

record QtRecordResult(
    string Record,
    int TruthCount,
    int MatchedCount,
    int PrN,
    double PrMean,
    double PrStd,
    int QrsN,
    double QrsMean,
    double QrsStd,
    int QtN,
    double QtMean,
    double QtStd
);

record RecordResult(
    string Record,
    int TruthCount,
    int DetectedCount,
    int TP,
    int FN,
    int FP,
    double Se,
    double Pp
);
