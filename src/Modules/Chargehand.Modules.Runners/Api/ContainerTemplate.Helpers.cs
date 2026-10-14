using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The helper containers of <see cref="ContainerTemplate"/>: the workspace, the host probe and the output-volume transfer.</summary>
public static partial class ContainerTemplate
{
    /// <summary>The script the workspace helper runs; the branch and the commit reach it only as arguments (<c>$1</c>, <c>$2</c>), never spliced into its text.
    /// <c>safe.directory</c> is needed because the read-only source belongs to another user. It is a global config in the helper's tmpfs (the root filesystem is
    /// read-only, so HOME moves to /tmp): <c>git clone</c> of a local path runs <c>git-upload-pack</c> as a child, which checks ownership itself and does not see
    /// <c>-c</c> or <c>GIT_CONFIG_*</c> (measured on a Linux host). The clone gets no hooks (<c>--template=</c>) and its own commits run none (<c>core.hooksPath</c>). The session image keeps the .NET CLI home and
    /// the NuGet package cache in /work (<c>DOTNET_CLI_HOME</c>, <c>NUGET_PACKAGES</c>), and the JDK variant (ADR 0048) Gradle's and Maven's: they are excluded here, so the stop snapshot's <c>git add -A</c> does not commit them.</summary>
    public const string WorkspaceScript =
        "set -e\n"
        + "export HOME=/tmp\n"
        + "git config --global safe.directory '*'\n"
        + "git clone --quiet --no-hardlinks --template= /src /work\n"
        + "mkdir -p /work/.git/info\n"
        + "printf '%s\\n' '/.dotnet/' '/.nuget/' '/.gradle/' '/.m2/' >> /work/.git/info/exclude\n"
        + "git -C /work checkout --quiet -b \"$1\" \"$2\"\n"
        + "git -C /work config user.name chargehand\n"
        + "git -C /work config user.email chargehand@localhost\n"
        + "git -C /work config core.hooksPath /dev/null\n";

    /// <summary>The arguments after <c>docker</c> for a workspace helper: foreground, removed when it ends, no network, a read-only source and one volume.</summary>
    public static IReadOnlyList<string> WorkspaceArgs(WorkspaceSpec spec)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.WorkVolume, "work volume");
        Check(SourcePathPattern(), spec.SourcePath, "source path (a plain absolute path)");
        Check(WorkspaceBranchPattern(), spec.Branch, "branch (chargehand/<name>)");
        Check(CommitPattern(), spec.Commit, "commit (7 to 40 hex characters)");
        return
        [
            "run", "--rm", "--init",
            "--name", PrepPrefix + spec.RunId,
            "--label", $"{RunLabel}={spec.RunId}",
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=256m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "128",
            "--memory", "512m",
            "--cpus", "1",
            "-v", $"{spec.SourcePath}:/src:ro",
            "-v", $"{spec.WorkVolume}:/work",
            "--entrypoint", "sh",
            spec.Image,
            "-c", WorkspaceScript, "prepare", spec.Branch, spec.Commit,
        ];
    }

    /// <summary>The script of the workspace helper for a bundle (ADR 0058): the clone comes from <c>/in/source.bundle</c>, which is a file and not another user's directory, so there is no ownership check to
    /// get past (no <c>safe.directory</c>). The bundle holds the pinned commit's history under <c>HEAD</c>; the clone is made without a checkout and the branch is created at the commit. Everything else is as
    /// <see cref="WorkspaceScript"/>: no hooks, the toolchains' caches excluded from the snapshot commit, the branch and the commit reach it only as arguments.</summary>
    public const string BundleWorkspaceScript =
        "set -e\n"
        + "export HOME=/tmp\n"
        + "git clone --quiet --no-hardlinks --template= --no-checkout /in/source.bundle /work\n"
        + "mkdir -p /work/.git/info\n"
        + "printf '%s\\n' '/.dotnet/' '/.nuget/' '/.gradle/' '/.m2/' >> /work/.git/info/exclude\n"
        + "git -C /work checkout --quiet -b \"$1\" \"$2\"\n"
        + "git -C /work config user.name chargehand\n"
        + "git -C /work config user.email chargehand@localhost\n"
        + "git -C /work config core.hooksPath /dev/null\n";

    /// <summary>The arguments after <c>docker</c> for a bundle workspace helper: as <see cref="WorkspaceArgs"/>, with the run's output volume read-only at <c>/in</c> in place of a source directory.</summary>
    public static IReadOnlyList<string> BundleWorkspaceArgs(BundleWorkspaceSpec spec)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.OutVolume, "out volume");
        Check(NamePattern(), spec.WorkVolume, "work volume");
        Check(WorkspaceBranchPattern(), spec.Branch, "branch (chargehand/<name>)");
        Check(CommitPattern(), spec.Commit, "commit (7 to 40 hex characters)");
        return
        [
            "run", "--rm", "--init",
            "--name", PrepPrefix + spec.RunId,
            "--label", $"{RunLabel}={spec.RunId}",
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=256m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "128",
            "--memory", "512m",
            "--cpus", "1",
            "-v", $"{spec.OutVolume}:/in:ro",
            "-v", $"{spec.WorkVolume}:/work",
            "--entrypoint", "sh",
            spec.Image,
            "-c", BundleWorkspaceScript, "prepare", spec.Branch, spec.Commit,
        ];
    }

    /// <summary>The markers between the three files the host probe's script prints.</summary>
    public const string ProbeLoadMarker = "#chargehand-loadavg";

    public const string ProbePressureMarker = "#chargehand-pressure";

    public const string ProbeCpusMarker = "#chargehand-cpus";

    private const string HostProbeScript =
        "cat /proc/meminfo\n"
        + "echo '" + ProbeLoadMarker + "'\n"
        + "cat /proc/loadavg\n"
        + "echo '" + ProbePressureMarker + "'\n"
        + "cat /proc/pressure/memory 2>/dev/null || true\n"
        + "echo '" + ProbeCpusMarker + "'\n"
        + "nproc\n";

    /// <summary>The arguments after <c>docker</c> for the host probe (ADR 0058): a runner on a Mac cannot read the engine's memory from its own <c>/proc</c>, because the engine runs in a virtual machine, so it
    /// asks a throwaway container, which sees the machine's. Foreground, removed when it ends, no network, no mount, a read-only root, nothing it could write to. It has no name and no label, so it can collide with
    /// nothing, and does not count against the runner's container limit.</summary>
    public static IReadOnlyList<string> HostProbeArgs(string image)
    {
        Check(ImagePattern(), image, "image (must be name@sha256:<64 hex> or a local image id)");
        return
        [
            "run", "--rm", "--init",
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "32",
            "--memory", "64m",
            "--cpus", "0.5",
            "--entrypoint", "sh",
            image,
            "-c", HostProbeScript,
        ];
    }

    /// <summary>The previous session's bundle, which a continuation session starts from (ADR 0044).</summary>
    public const string SeedFile = "seed.bundle";

    /// <summary>The repository at the pinned commit, as a git bundle, which the server sends to a runner that cannot see its checkouts (ADR 0058).</summary>
    public const string SourceBundleFile = "source.bundle";

    /// <summary>The files chargehand writes into a session's output volume.</summary>
    public static readonly IReadOnlyList<string> OutFilesIn = ["task.json", SeedFile, SourceBundleFile];

    /// <summary>The files chargehand reads out of it: the bundle, the report, the outcome, the running token tally, the handoff of a stopped session and the governor's log (CHARGEHAND-214). Nothing else leaves the volume.</summary>
    public static readonly IReadOnlyList<string> OutFilesOut = ["chargehand.bundle", "driven-report.json", "session-outcome.json", UsageFile, HandoffFile, ProgressFile, GovernorFile];

    /// <summary>What a stopped session leaves for the next one (ADR 0045): the facts as JSON and as the text that goes into the next prompt. A runner older than these names refuses to
    /// read them, so the server treats both as optional.</summary>
    public const string HandoffFile = "handoff.json";

    public const string ProgressFile = "progress.md";

    /// <summary>Every governor decision of a session, a JSON object per line (ADR 0045); the session writes it only when a governor ran.</summary>
    public const string GovernorFile = "governor.jsonl";

    /// <summary>The files of <see cref="OutFilesOut"/> that may be missing from the volume and that an older runner may refuse.</summary>
    public static readonly IReadOnlyList<string> OptionalOutFiles = [HandoffFile, ProgressFile, GovernorFile];

    /// <summary>The session's running token tally, rewritten while it works; the host polls it so the caps bind before the task ends.</summary>
    public const string UsageFile = "session-usage.json";

    /// <summary>The session's stream (<c>stream-json</c>, one event per line, the session's own credentials redacted): its transcript (ADR 0059). It is far too large to copy out whole, so it is not among
    /// <see cref="OutFilesOut"/>; the runner serves only the end of it, and only as a tail.</summary>
    public const string TranscriptFile = "stream.jsonl";

    /// <summary>The files the runner serves only as a tail of at most <see cref="MaxTailBytes"/>, never whole (ADR 0059).</summary>
    public static readonly IReadOnlyList<string> OutFilesTail = [TranscriptFile];

    /// <summary>The most bytes of a file's end one tail read returns.</summary>
    public const int MaxTailBytes = 256 * 1024;

    /// <summary>Exit code of the read helper when the file is not there.</summary>
    public const int OutFileMissing = 3;

    /// <summary>A transfer helper is named <c>chargehand-xfer-&lt;run&gt;</c>: one name per run, so a copy left under it by a killed call blocks every later transfer of that run.</summary>
    public const string TransferPrefix = "chargehand-xfer-";

    /// <summary>A workspace helper (from a source directory or from a bundle) is named <c>chargehand-prep-&lt;run&gt;</c>, with the same fixed-name risk as <see cref="TransferPrefix"/>.</summary>
    public const string PrepPrefix = "chargehand-prep-";

    private const string OutWriteScript = "set -e\ncat > \"/out/$1\"\n";
    private const string OutReadScript = "[ -f \"/out/$1\" ] || exit 3\nexec cat \"/out/$1\"\n";
    /// <summary>The tail helper's script: the name is <c>$1</c> and the byte count <c>$2</c>; exit 3 when the file is not there.</summary>
    public const string OutTailScript = "[ -f \"/out/$1\" ] || exit 3\nexec tail -c \"$2\" \"/out/$1\"\n";

    /// <summary>The arguments after <c>docker</c> for the helper that writes (stdin to the file) or reads (the file to stdout) one fixed-name file of the output volume:
    /// foreground, removed when it ends, no network, read-only root, the volume read-only for a read. The name reaches the script only as <c>$1</c>.</summary>
    public static IReadOnlyList<string> OutFileArgs(OutFileSpec spec, bool write)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.OutVolume, "out volume");
        if (!(write ? OutFilesIn : OutFilesOut).Contains(spec.Name, StringComparer.Ordinal))
            throw new ArgumentException($"'{spec.Name}' is not a file chargehand {(write ? "writes into" : "reads out of")} an output volume");
        return Helper(spec, write, write ? OutWriteScript : OutReadScript);
    }

    /// <summary>The same helper for the end of a file that is only ever read as a tail (<see cref="OutFilesTail"/>, ADR 0059): the volume read-only, the name as <c>$1</c> and the byte count, a plain
    /// number within <see cref="MaxTailBytes"/>, as <c>$2</c>.</summary>
    public static IReadOnlyList<string> OutTailArgs(OutFileSpec spec, int bytes)
    {
        Check(RunIdPattern(), spec.RunId, "run id");
        Check(ImagePattern(), spec.Image, "image (must be name@sha256:<64 hex> or a local image id)");
        Check(NamePattern(), spec.OutVolume, "out volume");
        if (!OutFilesTail.Contains(spec.Name, StringComparer.Ordinal))
            throw new ArgumentException($"'{spec.Name}' is not a file chargehand reads the end of");
        if (bytes is < 1 or > MaxTailBytes)
            throw new ArgumentException($"a tail is 1 to {MaxTailBytes} bytes", nameof(bytes));
        return [.. Helper(spec, write: false, OutTailScript), bytes.ToString(CultureInfo.InvariantCulture)];
    }

    private static IReadOnlyList<string> Helper(OutFileSpec spec, bool write, string script)
    {
        return
        [
            "run", "--rm", "--init", "-i",
            "--name", TransferPrefix + spec.RunId,
            "--label", $"{RunLabel}={spec.RunId}",
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,size=16m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", "10001:10001",
            "--pids-limit", "64",
            "--memory", "256m",
            "--cpus", "1",
            "-v", write ? $"{spec.OutVolume}:/out" : $"{spec.OutVolume}:/out:ro",
            "--entrypoint", "sh",
            spec.Image,
            "-c", script, "transfer", spec.Name,
        ];
    }
}
