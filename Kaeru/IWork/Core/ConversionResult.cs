using System;
using System.Collections.Generic;

namespace IWorkConverter.Core
{
    public sealed class ConversionResult
    {
        public bool Success;
        public string InputPath;
        public string OutputPath;
        public string Message = string.Empty;
        public readonly List<string> Notes = new List<string>();

        public void Note(string s) { if (!string.IsNullOrEmpty(s)) Notes.Add(s); }
    }
}
