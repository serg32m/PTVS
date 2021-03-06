// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis.Analyzer;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Interpreter.LegacyDB;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;
using Microsoft.Win32;

namespace Microsoft.PythonTools.Analysis {
    internal class PyLibAnalyzer : IDisposable {
        private const string AnalysisLimitsKey = @"Software\Microsoft\PythonTools\" + AssemblyVersionInfo.VSVersion + 
            @"\Analysis\StandardLibrary";

        private readonly string _id;
        private readonly Version _version;
        private readonly string _interpreter;
        private readonly List<PythonLibraryPath> _library;
        private readonly string _outDir;
        private readonly List<string> _baseDb;
        private readonly string _logPrivate, _logGlobal, _logDiagnostic;
        private readonly bool _dryRun;
        private readonly string _waitForAnalysis, _projectFile;
        private readonly int _repeatCount;

        private bool _all, _interactive;

        private FileStream _pidMarkerFile;

        private readonly AnalyzerStatusUpdater _updater;
        private readonly CancellationToken _cancel;
        private TextWriter _listener;
        internal readonly List<List<ModulePath>> _scrapeFileGroups, _analyzeFileGroups;
        private readonly HashSet<string> _treatPathsAsStandardLibrary;
        private IEnumerable<string> _readModulePath;

        private DateTime _startTime;
        private int _progressOffset;
        private int _progressTotal;

        private const string BuiltinName2x = "__builtin__.idb";
        private const string BuiltinName3x = "builtins.idb";
        private static readonly HashSet<string> SkipBuiltinNames = new HashSet<string> {
            "__main__"
        };

        private static void Help() {
            Console.WriteLine("Python Library Analyzer {0} ({1})",
                AssemblyVersionInfo.StableVersion,
                AssemblyVersionInfo.Version);
            Console.WriteLine("Generates a cached analysis database for a Python interpreter.");
            Console.WriteLine();
            Console.WriteLine(" /id         [GUID]             - specify GUID of the interpreter being used");
            Console.WriteLine(" /v[ersion]  [version]          - specify language version to be used (x.y format)");
            Console.WriteLine(" /interactive                   - start interactive analyzer");

            Console.WriteLine(" /py[thon]   [filename]         - full path to the Python interpreter to use");
            Console.WriteLine(" /lib[rary]  [directory]        - full path to the Python library to analyze");
            Console.WriteLine(" /outdir     [output dir]       - specify output directory for analysis (default " +
                              "is current dir)");
            Console.WriteLine(" /all                           - don't skip file groups that look up to date");

            Console.WriteLine(" /basedb     [input dir]        - specify directory for baseline analysis.");
            Console.WriteLine(" /log        [filename]         - write analysis log");
            Console.WriteLine(" /glog       [filename]         - write start/stop events");
            Console.WriteLine(" /diag       [filename]         - write detailed (CSV) analysis log");
            Console.WriteLine(" /dryrun                        - don't analyze, but write out list of files that " +
                              "would have been analyzed.");
            Console.WriteLine(" /wait       [identifier]       - wait for the specified analysis to complete.");
            Console.WriteLine(" /repeat     [count]            - repeat up to count times if needed (default 3).");
            Console.WriteLine(" /comment    [comment]          - provide descriptive text about why this analyzed was created");
#if DEBUG
            Console.WriteLine(" /unittest                      - run from tests, Debug.Listeners will be replaced");
#endif
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseArguments(IEnumerable<string> args) {
            string currentKey = null;

            using (var e = args.GetEnumerator()) {
                while (e.MoveNext()) {
                    if (e.Current.StartsWith("/")) {
                        if (currentKey != null) {
                            yield return new KeyValuePair<string, string>(currentKey, null);
                        }
                        currentKey = e.Current.Substring(1).Trim();
                    } else {
                        yield return new KeyValuePair<string, string>(currentKey, e.Current);
                        currentKey = null;
                    }
                }

                if (currentKey != null) {
                    yield return new KeyValuePair<string, string>(currentKey, null);
                }
            }
        }

        public static int Main(string[] args) {
            PyLibAnalyzer inst;
            try {
                inst = MakeFromArguments(args);
            } catch (ArgumentNullException ex) {
                Console.Error.WriteLine("{0} is a required argument", ex.Message);
                Help();
                return PythonTypeDatabase.InvalidArgumentExitCode;
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("'{0}' is not valid for {1}", ex.Message, ex.ParamName);
                Help();
                return PythonTypeDatabase.InvalidArgumentExitCode;
            } catch (IdentifierInUseException) {
                Console.Error.WriteLine("This interpreter is already being analyzed.");
                return PythonTypeDatabase.AlreadyGeneratingExitCode;
            } catch (InvalidOperationException ex) {
                Console.Error.WriteLine(ex.Message);
                Help();
                return PythonTypeDatabase.InvalidOperationExitCode;
            }

            using (inst) {
                return inst.Run().GetAwaiter().GetResult();
            }
        }

        private async Task<int> Run() {
#if DEBUG
            // Running with the debugger attached will skip the
            // unhandled exception handling to allow easier debugging.
            if (Debugger.IsAttached) {
                await RunWorker();
            } else {
#endif
                try {
                    await RunWorker();
                } catch (IdentifierInUseException) {
                    // Database is currently being analyzed
                    Console.Error.WriteLine("This interpreter is already being analyzed.");
                    return PythonTypeDatabase.AlreadyGeneratingExitCode;
                } catch (Exception e) {
                    Console.Error.WriteLine("Error during analysis: {0}{1}", Environment.NewLine, e.ToString());
                    LogToGlobal("FAIL_STDLIB" + Environment.NewLine + e.ToString());
                    TraceError("Analysis failed{0}{1}", Environment.NewLine, e.ToString());
                    return -10;
                }
#if DEBUG
            }
#endif

            LogToGlobal("DONE_STDLIB");

            return 0;
        }

        private async Task RunWorker() {
            if (_interactive) {
                var analyzer = new OutOfProcProjectAnalyzer(
                    Console.OpenStandardOutput(),
                    Console.OpenStandardInput(),
                    Console.Error.WriteLine
                );

                await analyzer.ProcessMessages();
            } else {
                WaitForOtherRun();

                while (true) {
                    try {
                        await StartTraceListener();
                        break;
                    } catch (IOException) {
                    }
                    await Task.Delay(20000);
                }

                LogToGlobal("START_STDLIB");

                for (int i = 0; i < _repeatCount && await Prepare(i == 0); ++i) {
                    await Scrape();
                    await Analyze();
                }
                await Epilogue();
            }
        }

        public PyLibAnalyzer(
            string id,
            Version langVersion,
            string interpreter,
            IEnumerable<PythonLibraryPath> library,
            List<string> baseDb,
            string outDir,
            string logPrivate,
            string logGlobal,
            string logDiagnostic,
            bool rescanAll,
            bool dryRun,
            string waitForAnalysis,
            int repeatCount,
            string projectFile = null,
            bool interactive = false
        ) {
            _id = id;
            _version = langVersion;
            _interpreter = interpreter;
            _baseDb = baseDb;
            _outDir = outDir;
            _logPrivate = logPrivate;
            _logGlobal = logGlobal;
            _logDiagnostic = logDiagnostic;
            _all = rescanAll;
            _dryRun = dryRun;
            _waitForAnalysis = waitForAnalysis;
            _repeatCount = repeatCount;
            _interactive = interactive;
            _projectFile = projectFile;

            _startTime = DateTime.UtcNow;

            _scrapeFileGroups = new List<List<ModulePath>>();
            _analyzeFileGroups = new List<List<ModulePath>>();
            _treatPathsAsStandardLibrary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _library = library != null ? library.ToList() : new List<PythonLibraryPath>();

            if (!String.IsNullOrWhiteSpace(_id) && !interactive) {
                _updater = new AnalyzerStatusUpdater(_id);
                // We worry about initialization exceptions here, specifically
                // that our identifier may already be in use.
                _updater.WaitForWorkerStarted();
                try {
                    _updater.ThrowPendingExceptions();
                    // Immediately inform any listeners that we've started running
                    // successfully.
                    _updater.UpdateStatus(0, 0, SecondsRunning, Strings.Analyzer_Initializing);
                } catch (InvalidOperationException) {
                    // Thrown when we run out of space in our shared memory
                    // block. Disable updates for this run.
                    _updater.Dispose();
                    _updater = null;
                }
            }
            // TODO: Link cancellation into the updater
            _cancel = CancellationToken.None;
        }

        private int SecondsRunning => (int)(DateTime.UtcNow - _startTime).TotalSeconds;

        public void LogToGlobal(string message) {
            if (!string.IsNullOrEmpty(_logGlobal)) {
                for (int retries = 10; retries > 0; --retries) {
                    try {
                        File.AppendAllText(_logGlobal,
                            string.Format("{0:s} {1} {2}{3}",
                                DateTime.Now,
                                message,
                                Environment.CommandLine,
                                Environment.NewLine
                            )
                        );
                        break;
                    } catch (DirectoryNotFoundException) {
                        // Create the directory and try again
                        Directory.CreateDirectory(Path.GetDirectoryName(_logGlobal));
                    } catch (IOException) {
                        // racing with someone else generating?
                        Thread.Sleep(25);
                    }
                }
            }
        }

        public void Dispose() {
            if (_updater != null) {
                _updater.Dispose();
            }
            if (_listener != null) {
                _listener.Flush();
                _listener.Close();
                _listener = null;
            }
            if (_pidMarkerFile != null) {
                _pidMarkerFile.Close();
            }
        }

        internal bool SkipUnchanged {
            get { return !_all; }
        }

        private static PyLibAnalyzer MakeFromArguments(IEnumerable<string> args) {
            var options = ParseArguments(args)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.InvariantCultureIgnoreCase);

            string value;

            string id = string.Empty;
            Version version = default(Version);
            string interpreter, outDir;
            var library = new List<PythonLibraryPath>();
            List<string> baseDb;
            string logPrivate, logGlobal, logDiagnostic, projectFile = null;
            bool rescanAll, dryRun, interactive = false;
            int repeatCount;

            var cwd = Environment.CurrentDirectory;

#if DEBUG
            if (options.ContainsKey("unittest")) {
                AssertListener.Initialize();
            }
#endif
            if (options.ContainsKey("interactive")) {
                interactive = true;
            }
            if (options.TryGetValue("proj", out value)) {
                projectFile = value;
            }


            if (!interactive) {
                if (options.TryGetValue("id", out value)) {
                    id = value;
                }

                if (!options.TryGetValue("version", out value) && !options.TryGetValue("v", out value)) {
                    throw new ArgumentNullException("version");
                } else if (!Version.TryParse(value, out version)) {
                    throw new ArgumentException(value, "version");
                }
            }

            if (!options.TryGetValue("python", out value) && !options.TryGetValue("py", out value)) {
                value = null;
            }
            if (!string.IsNullOrEmpty(value) && !PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "python");
            }
            interpreter = value;

            if (options.TryGetValue("library", out value) || options.TryGetValue("lib", out value)) {
                if (!PathUtils.IsValidPath(value)) {
                    throw new ArgumentException(value, "library");
                }
                if (Directory.Exists(value)) {
                    library.Add(new PythonLibraryPath(value, true, null));
                    var sitePackagesDir = Path.Combine(value, "site-packages");
                    if (Directory.Exists(sitePackagesDir)) {
                        library.Add(new PythonLibraryPath(sitePackagesDir, false, null));
                        library.AddRange(ModulePath.ExpandPathFiles(sitePackagesDir)
                            .Select(p => new PythonLibraryPath(p, false, null)));
                    }
                }
            }

            if (!options.TryGetValue("outdir", out value)) {
                value = cwd;
            }
            if (!PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "outdir");
            }
            outDir = PathUtils.GetAbsoluteDirectoryPath(cwd, value);

            if (!options.TryGetValue("basedb", out value)) {
                value = Environment.CurrentDirectory;
            }
            if (!PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "basedb");
            }
            baseDb = value.Split(';').Select(p => PathUtils.GetAbsoluteDirectoryPath(cwd, p)).ToList();

            // Private log defaults to in current directory
            if (!options.TryGetValue("log", out value)) {
                value = "AnalysisLog.txt";
            }
            if (!PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "log");
            }
            if (!Path.IsPathRooted(value)) {
                value = PathUtils.GetAbsoluteFilePath(outDir, value);
            }
            logPrivate = value;

            // Global log defaults to null - we don't write start/stop events.
            if (!options.TryGetValue("glog", out value)) {
                value = null;
            }
            if (!string.IsNullOrEmpty(value) && !PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "glog");
            }
            if (!string.IsNullOrEmpty(value) && !Path.IsPathRooted(value)) {
                value = PathUtils.GetAbsoluteFilePath(outDir, value);
            }
            logGlobal = value;

            // Diagnostic log defaults to registry setting or else we don't use it.
            if (!options.TryGetValue("diag", out value)) {
                using (var key = Registry.CurrentUser.OpenSubKey(AnalysisLimitsKey)) {
                    if (key != null) {
                        value = key.GetValue("LogPath") as string;
                    }
                }
            }
            if (!string.IsNullOrEmpty(value) && !PathUtils.IsValidPath(value)) {
                throw new ArgumentException(value, "diag");
            }
            if (!string.IsNullOrEmpty(value) && !Path.IsPathRooted(value)) {
                value = PathUtils.GetAbsoluteFilePath(outDir, value);
            }
            logDiagnostic = value;

            string waitForAnalysis;
            if (!options.TryGetValue("wait", out waitForAnalysis)) {
                waitForAnalysis = null;
            }

            rescanAll = options.ContainsKey("all");
            dryRun = options.ContainsKey("dryrun");

            if (!options.TryGetValue("repeat", out value) || !int.TryParse(value, out repeatCount)) {
                repeatCount = 3;
            }
            if (dryRun) {
                repeatCount = 1;
            }

            return new PyLibAnalyzer(
                id,
                version,
                interpreter,
                library,
                baseDb,
                outDir,
                logPrivate,
                logGlobal,
                logDiagnostic,
                rescanAll,
                dryRun,
                waitForAnalysis, 
                repeatCount,
                projectFile,
                interactive
            );
        }

        internal async Task StartTraceListener() {
            if (!PathUtils.IsValidPath(_logPrivate)) {
                return;
            }

            for (int retries = 10; retries > 0; --retries) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPrivate));
                    _listener = new StreamWriter(
                        new FileStream(_logPrivate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        Encoding.UTF8
                    );
                    break;
                } catch (IOException) {
                } catch (UnauthorizedAccessException) {
                }
                await Task.Delay(100);
            }

            if (_listener != null) {
                _listener.WriteLine();
                TraceInformation("Start analysis");
            } else {
                LogToGlobal(string.Format("WARN: Unable to log output to {0}", _logPrivate));
            }
        }

        private static bool IsTestPackage(ModulePath path) {
            var fn = "." + path.FullName;
            return fn.Contains(".test.") || fn.Contains(".tests.");
        }

        internal void WaitForOtherRun() {
            if (string.IsNullOrEmpty(_waitForAnalysis)) {
                return;
            }

            if (_updater != null) {
                _updater.UpdateStatus(0, 0, SecondsRunning, Strings.Analyzer_WaitingForStart);
            }

            bool everSeen = false;
            using (var evt = new AutoResetEvent(false))
            using (var listener = new AnalyzerStatusListener(d => {
                AnalysisProgress progress;
                if (d.TryGetValue(_waitForAnalysis, out progress)) {
                    everSeen = true;
                    string message;
                    if (string.IsNullOrEmpty(progress.Message)) {
                        message = Strings.Analyzer_WaitingForRefresh;
                    } else {
                        message = Strings.Analyzer_WaitingForRefresh_Message.FormatUI(progress.Message);
                    }
                    _updater.UpdateStatus(progress.Progress, progress.Maximum, SecondsRunning, message);
                } else if (everSeen) {
                    try {
                        evt.Set();
                    } catch (ObjectDisposedException) {
                        // Event arrived after timeout and/or disposal of
                        // listener.
                    }
                }
            }, TimeSpan.FromSeconds(1.0))) {
                if (!evt.WaitOne(TimeSpan.FromSeconds(60.0))) {
                    if (everSeen) {
                        // Running, but not finished yet
                        evt.WaitOne();
                    }
                }
            }
        }

        internal async Task<bool> Prepare(bool firstRun) {
            if (_updater != null) {
                _updater.UpdateStatus(0, 0, SecondsRunning, Strings.Analyzer_CollectingFiles);
            }

            if (_library.Any()) {
                if (firstRun) {
                    TraceWarning("Library was set explicitly - skipping path detection");
                }
            } else {
                List<PythonLibraryPath> library = null;

                // Execute the interpreter to get actual paths
                for (int retries = 3; retries >= 0; --retries) {
                    try {
                        library = await PythonLibraryPath.GetUncachedDatabaseSearchPathsAsync(_interpreter);
                        break;
                    } catch (InvalidOperationException ex) {
                        if (retries == 0) {
                            throw new InvalidOperationException("Cannot obtain search paths", ex);
                        }
                    } catch (Exception ex) {
                        if (ex.IsCriticalException()) {
                            throw;
                        }
                        throw new InvalidOperationException("Cannot obtain search paths", ex);
                    }
                    // May be a transient error, so try again shortly
                    await Task.Delay(500);
                }

                if (library == null) {
                    throw new InvalidOperationException("Cannot obtain search paths");
                }
                _library.AddRange(library);
            }

            if (File.Exists(_interpreter)) {
                foreach (var module in IncludeModulesFromModulePath) {
                    _library.AddRange(await GetSearchPathsFromModulePath(_interpreter, module));
                }
            }

            _treatPathsAsStandardLibrary.UnionWith(_library.Where(p => p.IsStandardLibrary).Select(p => p.Path));

            List<List<ModulePath>> fileGroups = null;
            for (int retries = 3; retries >= 0; --retries) {
                try {
                    fileGroups = PythonTypeDatabase.GetDatabaseExpectedModules(_version, _library).ToList();
                    break;
                } catch (UnauthorizedAccessException ex) {
                    if (retries == 0) {
                        throw new InvalidOperationException("Cannot obtain list of files", ex);
                    }
                } catch (Exception ex) {
                    if (ex.IsCriticalException()) {
                        throw;
                    }
                    throw new InvalidOperationException("Cannot obtain list of files", ex);
                }
                // May be a transient error, so try again shortly.
                await Task.Delay(500);
            }

            // Collect all test package files into their own groups. This
            // reduces completion information, generally for the better, and
            // significantly reduces time and memory usage while generating it.
            var testPackageGroups = new List<Tuple<List<ModulePath>, List<ModulePath>>>();
            foreach (var g in fileGroups) {
                var tg = g.Where(IsTestPackage).ToList();
                if (tg.Any()) {
                    g.RemoveAll(IsTestPackage);
                    testPackageGroups.Add(Tuple.Create(g, tg));
                }
            }

            // Insert test groups after the one they were obtained from
            foreach (var t in testPackageGroups) {
                int i = fileGroups.IndexOf(t.Item1);
                if (i >= 0 && i < fileGroups.Count) {
                    fileGroups.Insert(i + 1, t.Item2);
                } else {
                    fileGroups.Add(t.Item2);
                }
            }

            // HACK: Top-level modules in site-packages folders
            // Need to analyse them after the standard library and treat their
            // library paths as standard library so that output files are put at
            // the top level in the database.
            var sitePackageGroups = fileGroups
                .Where(g => g.Any() && PathUtils.GetFileOrDirectoryName(g[0].LibraryPath) == "site-packages")
                .ToList();
            fileGroups.RemoveAll(sitePackageGroups.Contains);
            fileGroups.InsertRange(1, sitePackageGroups);
            _treatPathsAsStandardLibrary.UnionWith(sitePackageGroups.Select(g => g[0].LibraryPath));


            var databaseVer = Path.Combine(_outDir, "database.ver");
            var databasePid = Path.Combine(_outDir, "database.pid");
            var databasePath = Path.Combine(_outDir, "database.path");

            if (!firstRun) {
                // We've already run once, so we only want to do a partial
                // update.
                _all = false;
            } else if (!PythonTypeDatabase.IsDatabaseVersionCurrent(_outDir)) {
                // Database is not the current version, so we have to
                // refresh all modules.
                _all = true;
            }

            var filesInDatabase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_dryRun) {
                TraceDryRun("WRITE;{0};{1}", databasePid, Process.GetCurrentProcess().Id);
                TraceDryRun("DELETE;{0}", databaseVer);
                TraceDryRun("WRITE;{0}", databasePath);

                // The output directory for a dry run may be completely invalid.
                // If the top level does not contain any .idb files, we won't
                // bother recursing.
                if (Directory.Exists(_outDir) &&
                    PathUtils.EnumerateFiles(_outDir, "*.idb", recurse: false).Any()) {
                    filesInDatabase.UnionWith(PathUtils.EnumerateFiles(_outDir, "*.idb"));
                }
            } else if (firstRun) {
                Directory.CreateDirectory(_outDir);

                try {
                    _pidMarkerFile = new FileStream(
                        databasePid,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete,
                        8,
                        FileOptions.DeleteOnClose
                    );
                } catch (IOException) {
                    // File exists, which means we are already being refreshed
                    // by another instance.
                    throw new IdentifierInUseException();
                }

                // Let exceptions propagate from here. If we can't write to this
                // file, we can't safely generate the DB.
                var pidString = Process.GetCurrentProcess().Id.ToString();
                var data = Encoding.UTF8.GetBytes(pidString);
                _pidMarkerFile.Write(data, 0, data.Length);
                _pidMarkerFile.Flush(true);
                // Don't close the file (because it will be deleted on close).
                // We will close it when we are disposed, or if we crash.

                try {
                    File.Delete(databaseVer);
                } catch (ArgumentException) {
                } catch (IOException) {
                } catch (NotSupportedException) {
                } catch (UnauthorizedAccessException) {
                }

                for (int retries = 3; retries >= 0; --retries) {
                    try {
                        PythonLibraryPath.WriteDatabaseSearchPaths(Path.Combine(_outDir, "database.path"), _library);
                        break;
                    } catch (IOException ex) {
                        if (retries == 0) {
                            throw new InvalidOperationException("Unable to cache search paths", ex);
                        }
                    } catch (Exception ex) {
                        throw new InvalidOperationException("Unable to cache search paths", ex);
                    }
                    // May be a transient error, so try again shortly.
                    await Task.Delay(500);
                }
            }

            if (!_dryRun) {
                filesInDatabase.UnionWith(PathUtils.EnumerateFiles(_outDir, "*.idb"));
            }

            // Store the files we want to keep separately, in case we decide to
            // delete the entire existing database.
            var filesToKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!_all) {
                var builtinModulePaths = await GetBuiltinModuleOutputFiles();
                if (builtinModulePaths.Any()) {
                    var interpreterTime = File.GetLastWriteTimeUtc(_interpreter);
                    var outOfDate = builtinModulePaths.Where(p => !File.Exists(p) || File.GetLastWriteTimeUtc(p) <= interpreterTime);
                    if (!outOfDate.Any()) {
                        filesToKeep.UnionWith(builtinModulePaths);
                    } else {
                        TraceInformation(
                            "Adding /all because the following built-in modules needed updating: {0}",
                            string.Join(";", outOfDate)
                        );
                        _all = true;
                    }
                } else {
                    // Failed to get builtin names, so don't delete anything
                    // from the main output directory.
                    filesToKeep.UnionWith(PathUtils.EnumerateFiles(_outDir, "*.idb", recurse: false));
                }
            }

            _progressTotal = 0;
            _progressOffset = 0;

            _scrapeFileGroups.Clear();
            var analyzeFileGroups = new List<List<ModulePath>>();
            var candidateScrapeFileGroups = new List<List<ModulePath>>();
            var candidateAnalyzeFileGroups = new List<List<ModulePath>>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fileGroup in fileGroups) {
                var toScrape = fileGroup.Where(mp => mp.IsCompiled && seen.Add(mp.ModuleName)).ToList();
                var toAnalyze = fileGroup.Where(mp => seen.Add(mp.ModuleName)).ToList();

                if (ShouldAnalyze(toScrape.Concat(toAnalyze))) {
                    if (!_all && _treatPathsAsStandardLibrary.Contains(fileGroup[0].LibraryPath)) {
                        _all = true;
                        TraceInformation("Adding /all because this group is builtin or stdlib");
                        // Include all the file groups we've already seen.
                        _scrapeFileGroups.InsertRange(0, candidateScrapeFileGroups);
                        analyzeFileGroups.InsertRange(0, candidateAnalyzeFileGroups);
                        _progressTotal += candidateScrapeFileGroups.Concat(candidateAnalyzeFileGroups).Sum(fg => fg.Count);
                        candidateScrapeFileGroups = null;
                        candidateAnalyzeFileGroups = null;
                    }

                    _progressTotal += toScrape.Count + toAnalyze.Count;

                    if (toScrape.Any()) {
                        _scrapeFileGroups.Add(toScrape);
                    }
                    if (toAnalyze.Any()) {
                        analyzeFileGroups.Add(toAnalyze);
                    }
                } else {
                    filesToKeep.UnionWith(fileGroup
                        .Where(mp => File.Exists(mp.SourceFile))
                        .Select(GetOutputFile));

                    if (candidateScrapeFileGroups != null) {
                        candidateScrapeFileGroups.Add(toScrape);
                    }
                    if (candidateAnalyzeFileGroups != null) {
                        candidateAnalyzeFileGroups.Add(toAnalyze);
                    }
                }
            }

            if (!_all) {
                filesInDatabase.ExceptWith(filesToKeep);
            }

            // Reorder analyzeFileGroups to improve quality of results for
            // later packages.
            var firstPackages = analyzeFileGroups
                .Where(g => _treatPathsAsStandardLibrary.Contains(g[0].LibraryPath))
                .ToList();
            analyzeFileGroups.RemoveAll(g => _treatPathsAsStandardLibrary.Contains(g[0].LibraryPath));
            foreach (var package in GetPackageOrder()) {
                var matching = analyzeFileGroups
                    .Where(g => g[0].ModuleName.StartsWith(package + ".") || g[0].ModuleName == package)
                    .ToList();
                firstPackages.AddRange(matching);
                analyzeFileGroups.RemoveAll(g => matching.Contains(g));
            }
            _analyzeFileGroups.Clear();
            _analyzeFileGroups.AddRange(firstPackages);
            _analyzeFileGroups.AddRange(analyzeFileGroups);

            // Scale file removal by 10 because it's much quicker than analysis.
            _progressTotal += filesInDatabase.Count / 10;
            Clean(filesInDatabase, 10);

            return _scrapeFileGroups.Any() || _analyzeFileGroups.Any() || _all;
        }

        internal static async Task<IEnumerable<PythonLibraryPath>> GetSearchPathsFromModulePath(
            string interpreter,
            string moduleName
        ) {
            using (var proc = ProcessOutput.RunHiddenAndCapture(
                interpreter,
                "-E",   // ignore environment
                "-B",
                "-c", string.Format("import {0}; print('\\n'.join({0}.__path__[1:]))", moduleName)
            )) {
                if (await proc != 0) {
                    return Enumerable.Empty<PythonLibraryPath>();
                }

                return proc.StandardOutputLines
                    .Where(Directory.Exists)
                    .Select(path => new PythonLibraryPath(path, false, moduleName + "."))
                    .ToList();
            }
        }

        bool ShouldAnalyze(IEnumerable<ModulePath> group) {
            if (_all) {
                return true;
            }

            foreach (var file in group.Where(f => File.Exists(f.SourceFile))) {
                var destPath = GetOutputFile(file);
                if (!File.Exists(destPath) ||
                    File.GetLastWriteTimeUtc(file.SourceFile) > File.GetLastWriteTimeUtc(destPath)) {
                    TraceInformation("Including {0} because {1} needs updating", file.LibraryPath, file.FullName);
                    return true;
                }
            }
            return false;
        }

        internal async Task<IEnumerable<string>> GetBuiltinModuleOutputFiles() {
            if (string.IsNullOrEmpty(_interpreter)) {
                return Enumerable.Empty<string>();
            }

            // Ignoring case because these will become file paths, even though
            // they are case-sensitive module names.
            var builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            builtinNames.Add(_version.Major == 3 ? BuiltinName3x : BuiltinName2x);
            using (var output = ProcessOutput.RunHiddenAndCapture(
                _interpreter,
                "-E", "-S", "-B",
                "-c", "import sys; print('\\n'.join(sys.builtin_module_names))"
            )) {
                if (await output != 0) {
                    TraceInformation("Getting builtin names");
                    TraceInformation("Command {0}", output.Arguments);
                    if (output.StandardErrorLines.Any()) {
                        TraceError("Errors{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, output.StandardErrorLines));
                    }
                    return Enumerable.Empty<string>();
                } else {
                    builtinNames = new HashSet<string>(output.StandardOutputLines);
                }
            }

            if (builtinNames.Contains("clr")) {
                bool isCli = false;
                using (var output = ProcessOutput.RunHiddenAndCapture(_interpreter,
                    "-E", "-S", "-B",
                    "-c", "import sys; print(sys.platform)"
                )) {
                    if (await output == 0) {
                        isCli = output.StandardOutputLines.Contains("cli");
                    }
                }
                if (isCli) {
                    // These should match IronPythonScraper.SPECIAL_MODULES
                    builtinNames.Remove("wpf");
                    builtinNames.Remove("clr");
                }
            }

            TraceVerbose("Builtin names are: {0}", string.Join(", ", builtinNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));

            return builtinNames
                .Where(n => !SkipBuiltinNames.Contains(n))
                .Where(PathUtils.IsValidPath)
                .Select(n => GetOutputFile(n));
        }

        internal void Clean(HashSet<string> files, int progressScale = 1) {
            if (_updater != null) {
                _updater.UpdateStatus(_progressOffset, _progressTotal, SecondsRunning, Strings.Analyzer_CleaningFiles);
            }

            int modCount = 0;
            TraceInformation("Deleting {0} files", files.Count);
            bool traceDelete = files.Count < 10;
            foreach (var file in files) {
                if (_updater != null && ++modCount >= progressScale) {
                    modCount = 0;
                    _updater.UpdateStatus(++_progressOffset, _progressTotal, SecondsRunning, Strings.Analyzer_CleaningFiles);
                }

                if (_dryRun) {
                    TraceDryRun("DELETE:{0}", file);
                } else {
                    if (traceDelete) {
                        // Extra logging for occasional issues where a few files
                        // always get deleted.
                        TraceInformation("Deleting \"{0}\"", file);
                    } else {
                        TraceVerbose("Deleting \"{0}\"", file);
                    }
                    try {
                        File.Delete(file);
                        File.Delete(file + ".$memlist");
                        var dirName = Path.GetDirectoryName(file);
                        if (!Directory.EnumerateFileSystemEntries(dirName, "*", SearchOption.TopDirectoryOnly).Any()) {
                            TraceVerbose("Removing empty directory \"{0}\"", dirName);
                            Directory.Delete(dirName);
                        }
                    } catch (ArgumentException) {
                    } catch (IOException) {
                    } catch (UnauthorizedAccessException) {
                    } catch (NotSupportedException) {
                    }
                }
            }
        }


        internal string PythonScraperPath {
            get {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var file = Path.Combine(dir, "PythonScraper.py");
                return file;
            }
        }

        internal string ExtensionScraperPath {
            get {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var file = Path.Combine(dir, "ExtensionScraper.py");
                return file;
            }
        }

        private Dictionary<string, int> GetCallDepthOverrides() {
            var res = new Dictionary<string, int>();

            var values = ConfigurationManager.AppSettings.Get("NoCallSiteAnalysis");
            if (!string.IsNullOrEmpty(values)) {
                TraceInformation("NoCallSiteAnalysis = {0}", values);
                foreach (var value in values.Split(',', ';').Where(n => !string.IsNullOrWhiteSpace(n))) {
                    res[value] = 0;
                }
            }

            var r = new Regex(@"^(?<module>[\w\.]+)\.CallDepth", RegexOptions.IgnoreCase);
            Match m;
            foreach (var key in ConfigurationManager.AppSettings.AllKeys) {
                if ((m = r.Match(key)).Success) {
                    int depth;
                    if (int.TryParse(ConfigurationManager.AppSettings[key], out depth)) {
                        res[m.Groups["module"].Value] = depth;
                    } else {
                        TraceWarning("Failed to parse \"{0}={1}\" from config file", key, ConfigurationManager.AppSettings[key]);
                    }
                }
            }

            foreach (var keyValue in res.OrderBy(kv => kv.Key)) {
                TraceInformation("{0}.CallDepth = {1}", keyValue.Key, keyValue.Value);
            }

            return res;
        }

        private IEnumerable<string> GetSkipModules() {
            var res = new HashSet<string>();

            var r = new Regex(@"^(?<module>[\w\.]+)\.Skip", RegexOptions.IgnoreCase);
            Match m;
            foreach (var key in ConfigurationManager.AppSettings.AllKeys) {
                if ((m = r.Match(key)).Success) {
                    bool value;
                    if (bool.TryParse(ConfigurationManager.AppSettings[key], out value) && value) {
                        TraceInformation("{0}.Skip = True", m.Groups["module"].Value);
                        yield return m.Groups["module"].Value;
                    }
                }
            }
        }

        private IEnumerable<string> GetPackageOrder() {
            var values = ConfigurationManager.AppSettings.Get("PackageOrder");
            if (string.IsNullOrEmpty(values)) {
                return Enumerable.Empty<string>();
            }

            return values.Split(',', ';')
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrEmpty(n));
        }



        private IEnumerable<string> IncludeModulesFromModulePath {
            get {
                if (_readModulePath == null) {
                    var values = ConfigurationManager.AppSettings.Get("IncludeModulesFromModulePath");
                    if (string.IsNullOrEmpty(values)) {
                        _readModulePath = Enumerable.Empty<string>();
                    } else {
                        TraceInformation("IncludeModulesFromModulePath = {0}", values);
                        _readModulePath = values.Split(',', ';')
                            .Select(n => n?.Trim())
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToArray();
                    }
                }
                return _readModulePath;
            }
        }

        internal async Task Scrape() {
            if (string.IsNullOrEmpty(_interpreter)) {
                return;
            }

            if (_updater != null) {
                _updater.UpdateStatus(_progressOffset, _progressTotal, SecondsRunning, Strings.Analyzer_ScrapingStdLib);
            }

            if (_all) {
                if (_dryRun) {
                    TraceDryRun("Scrape builtin modules");
                } else {
                    // Scape builtin Python types
                    using (var output = ProcessOutput.RunHiddenAndCapture(_interpreter, "-B", PythonScraperPath, _outDir, _baseDb.First())) {
                        TraceInformation("Scraping builtin modules");
                        TraceInformation("Command: {0}", output.Arguments);
                        await output;

                        if (output.StandardOutputLines.Any()) {
                            TraceInformation("Output{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, output.StandardOutputLines));
                        }
                        if (output.StandardErrorLines.Any()) {
                            TraceWarning("Errors{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, output.StandardErrorLines));
                        }

                        if (output.ExitCode != 0) {
                            if (output.ExitCode.HasValue) {
                                TraceError("Failed to scrape builtin modules (Exit Code: {0})", output.ExitCode);
                            } else {
                                TraceError("Failed to scrape builtin modules");
                            }
                            throw new InvalidOperationException("Failed to scrape builtin modules");
                        } else {
                            TraceInformation("Scraped builtin modules");
                        }
                    }
                }
            }

            foreach (var file in _scrapeFileGroups.SelectMany()) {
                Debug.Assert(file.IsCompiled);

                if (_updater != null) {
                    _updater.UpdateStatus(_progressOffset++, _progressTotal, SecondsRunning,
                        Strings.Analyzer_Scraping.FormatUI(PathUtils.GetFileOrDirectoryName(file.LibraryPath)));
                }

                var destFile = Path.ChangeExtension(GetOutputFile(file), null);
                if (_dryRun) {
                    TraceDryRun("SCRAPE;{0};{1}.idb", file.SourceFile, PathUtils.CreateFriendlyDirectoryPath(_outDir, destFile));
                } else {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile));

                    // Provide a sys.path entry to ensure we can import the
                    // extension module. For cases where this is necessary, it
                    // probably means that the user can't import the module
                    // either, but they may have some other way of resolving it
                    // at runtime.
                    var scrapePath = Path.GetDirectoryName(file.SourceFile);
                    foreach (var part in file.ModuleName.Split('.').Reverse().Skip(1)) {
                        if (Path.GetFileName(scrapePath).Equals(part, StringComparison.Ordinal)) {
                            scrapePath = Path.GetDirectoryName(scrapePath);
                        } else {
                            break;
                        }
                    }

                    var prefixDir = Path.GetDirectoryName(_interpreter);
                    var pathVar = string.Format("{0};{1}", Environment.GetEnvironmentVariable("PATH"), prefixDir);
                    var arguments = new [] { "-B", ExtensionScraperPath, "scrape", file.ModuleName, scrapePath, destFile };
                    var env = new[] { new KeyValuePair<string, string>("PATH", pathVar) };

                    using (var output = ProcessOutput.Run(_interpreter, arguments, prefixDir, env, false, null)) {
                        TraceInformation("Scraping {0}", file.ModuleName);
                        TraceInformation("Command: {0}", output.Arguments);
                        TraceVerbose("environ['Path'] = {0}", pathVar);
                        await output;

                        if (output.StandardOutputLines.Any()) {
                            TraceInformation("Output{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, output.StandardOutputLines));
                        }
                        if (output.StandardErrorLines.Any()) {
                            TraceWarning("Errors{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, output.StandardErrorLines));
                        }

                        if (output.ExitCode != 0) {
                            if (output.ExitCode.HasValue) {
                                TraceError("Failed to scrape {1} (Exit code: {0})", output.ExitCode, file.ModuleName);
                            } else {
                                TraceError("Failed to scrape {0}", file.ModuleName);
                            }
                        } else {
                            TraceVerbose("Scraped {0}", file.ModuleName);
                        }
                    }

                    // Ensure that the output file exists, otherwise the DB will
                    // never appear to be up to date.
                    var expected = GetOutputFile(file);
                    if (!File.Exists(expected)) {
                        using (var writer = new FileStream(expected, FileMode.Create, FileAccess.ReadWrite)) {
                            new Pickler(writer).Dump(new Dictionary<string, object> {
                                { "members", new Dictionary<string, object>() },
                                { "doc", "Could not import compiled module" }
                            });
                        }
                    }
                }
            }
            if (_scrapeFileGroups.Any()) {
                TraceInformation("Scraped {0} files", _scrapeFileGroups.SelectMany().Count());
            }
        }

        private static bool ContainsModule(HashSet<string> modules, string moduleName) {
            foreach (var name in ModulePath.GetParents(moduleName)) {
                if (modules.Contains(name)) {
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<string> GetDatabasePaths(string outDir) {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (seen.Add(_outDir)) {
                yield return _outDir;
            }

            if (seen.Add(outDir)) {
                yield return outDir;
            }

            foreach (var d in PathUtils.EnumerateDirectories(_outDir, recurse: false)) {
                if (seen.Add(d)) {
                    yield return d;
                }
            }

            foreach (var d in _baseDb.Skip(1)) {
                if (seen.Add(d)) {
                    yield return d;
                }
            }
        }

        internal Task Analyze() {
            if (_updater != null) {
                _updater.UpdateStatus(_progressOffset, _progressTotal, SecondsRunning, Strings.Analyzer_Starting);
            }

            if (!string.IsNullOrEmpty(_logDiagnostic) && AnalysisLog.Output == null) {
                try {
                    AnalysisLog.Output = _logDiagnostic;
                    AnalysisLog.AsCSV = _logDiagnostic.EndsWith(".csv", StringComparison.InvariantCultureIgnoreCase);
                } catch (Exception ex) {
                    TraceWarning("Failed to open \"{0}\" for logging{1}{2}", _logDiagnostic, Environment.NewLine, ex.ToString());
                }
            }

            var callDepthOverrides = GetCallDepthOverrides();
            var skipModules = new HashSet<string>(GetSkipModules(), StringComparer.Ordinal);

            foreach (var files in _analyzeFileGroups) {
                if (_cancel.IsCancellationRequested) {
                    break;
                }

                if (files.Count == 0) {
                    continue;
                }

                var outDir = GetOutputDir(files[0]);

                if (_dryRun) {
                    foreach (var file in files) {
                        if (ContainsModule(skipModules, file.ModuleName)) {
                            continue;
                        }

                        Debug.Assert(!file.IsCompiled);
                        var idbFile = PathUtils.CreateFriendlyDirectoryPath(
                            _outDir,
                            Path.Combine(outDir, file.ModuleName)
                        );
                        TraceDryRun("ANALYZE;{0};{1}.idb", file.SourceFile, idbFile);
                    }
                    continue;
                }

                Directory.CreateDirectory(outDir);

                TraceInformation("Start group \"{0}\" with {1} files", files[0].LibraryPath, files.Count);
                AnalysisLog.StartFileGroup(files[0].LibraryPath, files.Count);
                Console.WriteLine("Now analyzing: {0}", files[0].LibraryPath);
                string currentLibrary;
                if (_treatPathsAsStandardLibrary.Contains(files[0].LibraryPath)) {
                    currentLibrary = "standard library";
                } else {
                    currentLibrary = PathUtils.GetFileOrDirectoryName(files[0].LibraryPath);
                }

                using (var factory = PythonInterpreterFactoryWithDatabase.CreateFromDatabase(_version, outDir))
                using (var projectState = PythonAnalyzer.CreateAsync(factory).WaitAndUnwrapExceptions()) {
                    int? mostItemsInQueue = null;
                    if (_updater != null) {
                        projectState.SetQueueReporting(itemsInQueue => {
                            if (itemsInQueue > (mostItemsInQueue ?? 0)) {
                                mostItemsInQueue = itemsInQueue;
                            }

                            if (mostItemsInQueue > 0) {
                                var progress = (files.Count * (mostItemsInQueue - itemsInQueue)) / mostItemsInQueue;
                                _updater.UpdateStatus(_progressOffset + (progress ?? 0), _progressTotal, SecondsRunning,
                                    Strings.Analyzer_Analyzing.FormatUI(currentLibrary));
                            } else {
                                _updater.UpdateStatus(_progressOffset + files.Count, _progressTotal, SecondsRunning,
                                    Strings.Analyzer_Analyzing.FormatUI(currentLibrary));
                            }
                        }, 10);
                    }

                    try {
                        using (var key = Registry.CurrentUser.OpenSubKey(AnalysisLimitsKey)) {
                            projectState.Limits = AnalysisLimits.LoadFromStorage(key, defaultToStdLib: true);
                        }
                    } catch (SecurityException) {
                        projectState.Limits = AnalysisLimits.GetStandardLibraryLimits();
                    } catch (UnauthorizedAccessException) {
                        projectState.Limits = AnalysisLimits.GetStandardLibraryLimits();
                    } catch (IOException) {
                        projectState.Limits = AnalysisLimits.GetStandardLibraryLimits();
                    }

                    var items = files.Select(f => new AnalysisItem(f)).ToList();

                    foreach (var item in items) {
                        if (_cancel.IsCancellationRequested) {
                            break;
                        }

                        item.Entry = projectState.AddModule(item.ModuleName, item.SourceFile);

                        foreach (var name in ModulePath.GetParents(item.ModuleName, includeFullName: true)) {
                            int depth;
                            if (callDepthOverrides.TryGetValue(name, out depth)) {
                                TraceVerbose("Set CallDepthLimit to 0 for {0}", item.ModuleName);
                                item.Entry.Properties[AnalysisLimits.CallDepthKey] = depth;
                                break;
                            }
                        }
                    }

                    foreach (var item in items) {
                        if (_cancel.IsCancellationRequested) {
                            break;
                        }

                        if (ContainsModule(skipModules, item.ModuleName)) {
                            continue;
                        }

                        if (_updater != null) {
                            _updater.UpdateStatus(_progressOffset, _progressTotal, SecondsRunning,
                                Strings.Analyzer_Parsing.FormatUI(currentLibrary));
                        }
                        try {
                            var sourceUnit = new FileStream(item.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var errors = new CollectingErrorSink();
                            var opts = new ParserOptions() { BindReferences = true, ErrorSink = errors };

                            TraceVerbose("Parsing \"{0}\" (\"{1}\")", item.ModuleName, item.SourceFile);
                            item.Tree = Parser.CreateParser(sourceUnit, _version.ToLanguageVersion(), opts).ParseFile();
                            if (errors.Errors.Any() || errors.Warnings.Any()) {
                                TraceWarning("File \"{0}\" (\"{1}\") contained parse errors", item.ModuleName, item.SourceFile);
                                TraceInformation(string.Join(Environment.NewLine, errors.Errors.Concat(errors.Warnings)
                                    .Select(er => string.Format("{0} {1}", er.Span, er.Message))));
                            }
                        } catch (Exception ex) {
                            TraceError("Error parsing \"{0}\" \"{1}\"{2}{3}", item.ModuleName, item.SourceFile, Environment.NewLine, ex.ToString());
                        }
                    }

                    TraceInformation("Parsing complete");

                    foreach (var item in items) {
                        if (_cancel.IsCancellationRequested) {
                            break;
                        }

                        if (item.Tree != null) {
                            using (var parse = item.Entry.BeginParse()) {
                                parse.Tree = item.Tree;
                                parse.Complete();
                            }
                        }
                    }

                    foreach (var item in items) {
                        if (_cancel.IsCancellationRequested) {
                            break;
                        }

                        try {
                            if (item.Tree != null) {
                                TraceInformation("Analyzing \"{0}\" (\"{1}\")", item.ModuleName, item.SourceFile);
                                item.Entry.Analyze(_cancel, true);
                                TraceVerbose("Analyzed \"{0}\"", item.SourceFile);
                            }
                        } catch (Exception ex) {
                            TraceError("Error analyzing \"{0}\" \"{1}\"{2}{3}", item.ModuleName, item.SourceFile, Environment.NewLine, ex.ToString());
                        }
                    }

                    if (items.Count > 0 && !_cancel.IsCancellationRequested) {
                        TraceInformation("Starting analysis of {0} modules", items.Count);
                        items[0].Entry.AnalysisGroup.AnalyzeQueuedEntries(_cancel);
                        TraceInformation("Analysis complete");
                    }

                    if (_cancel.IsCancellationRequested) {
                        break;
                    }

                    TraceInformation("Saving group \"{0}\"", files[0].LibraryPath);
                    if (_updater != null) {
                        _progressOffset += files.Count;
                        _updater.UpdateStatus(_progressOffset, _progressTotal, SecondsRunning,
                            Strings.Analyzer_Saving.FormatUI(currentLibrary));
                    }
                    Directory.CreateDirectory(outDir);
                    var saver = new SaveAnalysis();
                    saver.Save(projectState, outDir);
                    foreach (var err in saver.Errors) {
                        TraceError("{0}", err);
                    }
                    TraceInformation("End of group \"{0}\"", files[0].LibraryPath);
                    AnalysisLog.EndFileGroup();

                    AnalysisLog.Flush();
                }
            }

            // Lets us have an awaitable function, even though it doesn't need
            // to be async yet. This helps keep the interfaces consistent.
            return Task.FromResult<object>(null);
        }

        internal Task Epilogue() {
            if (_dryRun) {
                TraceDryRun("WRITE;{0};{1}", Path.Combine(_outDir, "database.ver"), PythonTypeDatabase.CurrentVersion);
            } else {
                try {
                    File.WriteAllText(Path.Combine(_outDir, "database.ver"), PythonTypeDatabase.CurrentVersion.ToString());
                } catch (ArgumentException) {
                } catch (IOException) {
                } catch (NotSupportedException) {
                } catch (SecurityException) {
                } catch (UnauthorizedAccessException) {
                }

                if (_pidMarkerFile != null) {
                    _pidMarkerFile.Close();
                    _pidMarkerFile = null;
                }
            }

            // Lets us have an awaitable function, even though it doesn't need
            // to be async yet. This helps keep the interfaces consistent.
            return Task.FromResult<object>(null);
        }

        private string GetOutputDir(ModulePath file) {
            if (_treatPathsAsStandardLibrary.Contains(file.LibraryPath)) {
                return _outDir;
            } else {
                return Path.Combine(_outDir, Regex.Replace(
                    PathUtils.TrimEndSeparator(PathUtils.GetFileOrDirectoryName(file.LibraryPath)),
                    @"[.\\/\s]",
                    "_"
                ));
            }
        }

        private string GetOutputFile(string builtinName) {
            return PathUtils.GetAbsoluteFilePath(_outDir, builtinName + ".idb");
        }

        private string GetOutputFile(ModulePath file) {
            return PathUtils.GetAbsoluteFilePath(GetOutputDir(file), file.ModuleName + ".idb");
        }

        class AnalysisItem {
            readonly ModulePath _path;

            public IPythonProjectEntry Entry { get; set; }
            public PythonAst Tree { get; set; }

            public AnalysisItem(ModulePath path) {
                _path = path;
            }

            public string ModuleName { get { return _path.ModuleName; } }
            public string SourceFile { get { return _path.SourceFile; } }
        }


        internal void TraceInformation(string message, params object[] args) {
            if (_listener != null) {
                _listener.WriteLine(DateTime.Now.ToString("s") + ": " + string.Format(message, args));
                _listener.Flush();
            }
        }

        internal void TraceWarning(string message, params object[] args) {
            if (_listener != null) {
                _listener.WriteLine(DateTime.Now.ToString("s") + ": [WARNING] " + string.Format(message, args));
                _listener.Flush();
            }
        }

        internal void TraceError(string message, params object[] args) {
            if (_listener != null) {
                _listener.WriteLine(DateTime.Now.ToString("s") + ": [ERROR] " + string.Format(message, args));
                _listener.Flush();
            }
        }

        [Conditional("DEBUG")]
        internal void TraceVerbose(string message, params object[] args) {
            if (_listener != null) {
                _listener.WriteLine(DateTime.Now.ToString("s") + ": [VERBOSE] " + string.Format(message, args));
                _listener.Flush();
            }
        }

        internal void TraceDryRun(string message, params object[] args) {
            Console.WriteLine(message, args);
            TraceInformation(message, args);
        }

#if DEBUG
        class AssertListener : TraceListener {
            private AssertListener() {
            }

            public override string Name {
                get { return "Microsoft.PythonTools.AssertListener"; }
                set { }
            }

            public static bool LogObjectDisposedExceptions { get; set; } = true;

            public static void Initialize() {
                var listener = new AssertListener();
                if (null == Debug.Listeners[listener.Name]) {
                    Debug.Listeners.Add(listener);
                    Debug.Listeners.Remove("Default");

                    AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
                }
            }

            static void CurrentDomain_FirstChanceException(object sender, FirstChanceExceptionEventArgs e) {
                if (e.Exception is NullReferenceException || (e.Exception is ObjectDisposedException && LogObjectDisposedExceptions)) {
                    // Exclude safe handle messages because they are noisy
                    if (!e.Exception.Message.Contains("Safe handle has been closed")) {
                        var log = new EventLog("Application");
                        log.Source = "Application Error";
                        log.WriteEntry(
                            "First-chance exception: " + e.Exception.ToString(),
                            EventLogEntryType.Warning
                        );
                    }
                }
            }

            public override void Fail(string message) {
                Fail(message, null);
            }

            public override void Fail(string message, string detailMessage) {
                Trace.WriteLine("Debug.Assert failed in analyzer");
                if (!string.IsNullOrEmpty(message)) {
                    Trace.WriteLine(message);
                } else {
                    Trace.WriteLine("(No message provided)");
                }
                if (!string.IsNullOrEmpty(detailMessage)) {
                    Trace.WriteLine(detailMessage);
                }
                var trace = new StackTrace(true);
                bool seenDebugAssert = false;
                foreach (var frame in trace.GetFrames()) {
                    var mi = frame.GetMethod();
                    if (!seenDebugAssert) {
                        seenDebugAssert = (mi.DeclaringType == typeof(Debug) &&
                            (mi.Name == "Assert" || mi.Name == "Fail"));
                    } else if (mi.DeclaringType == typeof(System.RuntimeMethodHandle)) {
                        break;
                    } else {
                        var filename = frame.GetFileName();
                        Console.WriteLine(string.Format(
                            " at {0}.{1}({2}) in {3}:line {4}",
                            mi.DeclaringType.FullName,
                            mi.Name,
                            string.Join(", ", mi.GetParameters().Select(p => p.ToString())),
                            filename ?? "<unknown>",
                            frame.GetFileLineNumber()
                        ));
                        if (File.Exists(filename)) {
                            try {
                                Console.WriteLine(
                                    "    " +
                                    File.ReadLines(filename).ElementAt(frame.GetFileLineNumber() - 1).Trim()
                                );
                            } catch {
                            }
                        }
                    }
                }

                message = string.IsNullOrEmpty(message) ? "Debug.Assert failed" : message;
                if (Debugger.IsAttached) {
                    Debugger.Break();
                }

                Console.WriteLine(message);
            }

            public override void WriteLine(string message) {
            }

            public override void Write(string message) {
            }
        }
#endif
    }
}