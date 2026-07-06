using System;
using System.Collections.Generic;

namespace ECGMonitor
{
    public class EcgEvent
    {
        public string Type { get; set; } = "";
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public string Detail { get; set; } = "";
    }

    public class EcgSession
    {
        public string PatientName { get; set; } = "";
        public string PatientAge { get; set; } = "";
        public string PatientSex { get; set; } = "";
        public string PatientId { get; set; } = "";
        public string DoctorName { get; set; } = "";
        public string ClinicalNotes { get; set; } = "";
        public DateTime RecordedAt { get; set; }
        public double DurationSeconds { get; set; }
        public List<double> Times { get; set; } = new();
        public List<double> Amplitudes { get; set; } = new();
        public List<EcgEvent> Events { get; set; } = new();
        public string AvgHeartRate { get; set; } = "--";
        public string RhythmSummary { get; set; } = "--";
    }
}
