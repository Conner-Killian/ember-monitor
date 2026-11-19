// EmberSign: creates the update-signing key and signs release files.
//
//   dotnet run --project tools/EmberSign -- keygen           create the key pair (once)
//   dotnet run --project tools/EmberSign -- pubkey           print the public key
//   dotnet run --project tools/EmberSign -- sign <files…>    write <file>.sig next to each file
//   dotnet run --project tools/EmberSign -- verify <file>    check <file>.sig against the app's trusted keys
//   dotnet run --project tools/EmberSign -- serve <dir> <port>   local HTTP server (update tests)
//
// The private key never belongs in the repository. It defaults to
// %USERPROFILE%\.ember-signing\update-signing-key.pem (override with EMBER_SIGNING_KEY), and keygen
// refuses to write it inside a git working tree. Back it up somewhere safe: without it, installed
// copies of Ember can't accept updates.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ember.Services;

static string KeyPath() =>
    Environment.GetEnvironmentVariable("EMBER_SIGNING_KEY") is { Length: > 0 } p ? p
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ember-signing", "update-signing-key.pem");

static string? GitRoot(string path)
{
    for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
    return null;
}

static ECDsa LoadKey()
{
    var path = KeyPath();
    if (!File.Exists(path)) throw new FileNotFoundException($"Signing key not found at {path}. Run 'keygen' first or set EMBER_SIGNING_KEY.");
    var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(path));
    return ec;
}

static string PublicKey(ECDsa ec) => Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());

static int Fail(string msg) { Console.Error.WriteLine("error: " + msg); return 1; }

if (args.Length == 0) { Console.WriteLine("usage: EmberSign keygen | pubkey | sign <files…> | verify <files…> | serve <dir> <port>"); return 2; }

try
{
    switch (args[0])
    {
        case "keygen":
        {
            var path = KeyPath();
            if (File.Exists(path)) return Fail($"a key already exists at {path}; refusing to overwrite it");
            if (GitRoot(Path.GetDirectoryName(path)!) is string repo) return Fail($"{path} is inside the git repository {repo}; keep the key outside it");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, ec.ExportPkcs8PrivateKeyPem() + "\n");
            if (OperatingSystem.IsWindows())
            {
                // Only the current user may read it (no inherited access for other accounts).
                var who = Environment.UserDomainName + "\\" + Environment.UserName;
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls",
                    $"\"{path}\" /inheritance:r /grant:r \"{who}:F\"") { UseShellExecute = false, RedirectStandardOutput = true })!;
                p.WaitForExit();
                if (p.ExitCode != 0) Console.Error.WriteLine("warning: couldn't restrict the key file's permissions");
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine($"Private key: {path}");
            Console.WriteLine($"Public key:  {PublicKey(ec)}");
            return 0;
        }
        case "pubkey":
        {
            using var ec = LoadKey();
            Console.WriteLine(PublicKey(ec));
            return 0;
        }
        case "sign":
        {
            using var ec = LoadKey();
            var pub = PublicKey(ec);
            bool trusted = UpdateSignature.TrustedKeys.Contains(pub);
            foreach (var file in args.Skip(1))
            {
                if (GitRoot(file) is string && file.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) return Fail("refusing to sign a key file");
                using var fs = File.OpenRead(file);
                var name = Path.GetFileName(file);
                var hash = UpdateSignature.Hash(fs);
                var sig = UpdateSignature.Sign(ec, name, hash);
                if (!UpdateSignature.Verify(name, hash, sig, new[] { pub })) return Fail($"self-check failed for {name}");
                File.WriteAllText(file + ".sig", sig + "\n");
                Console.WriteLine($"signed {name}");
            }
            if (!trusted) Console.Error.WriteLine("warning: this key is NOT in UpdateSignature.TrustedKeys, so the app would reject these signatures");
            return 0;
        }
        case "verify":
        {
            int bad = 0;
            foreach (var file in args.Skip(1))
            {
                using var fs = File.OpenRead(file);
                var ok = File.Exists(file + ".sig") &&
                         UpdateSignature.Verify(Path.GetFileName(file), UpdateSignature.Hash(fs), File.ReadAllText(file + ".sig"));
                Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {Path.GetFileName(file)}");
                if (!ok) bad++;
            }
            return bad == 0 ? 0 : 1;
        }
        case "serve":
        {
            // Minimal static file server bound to 127.0.0.1 (tests only).
            var root = Path.GetFullPath(args[1]);
            var listener = new TcpListener(IPAddress.Loopback, int.Parse(args[2]));
            listener.Start();
            Console.WriteLine($"serving {root} on http://127.0.0.1:{args[2]}/");
            while (true)
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = reader.ReadLine() ?? "";
                while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                var rel = Uri.UnescapeDataString(request.Split(' ').ElementAtOrDefault(1)?.TrimStart('/') ?? "");
                var file = Path.GetFullPath(Path.Combine(root, rel));
                bool ok = file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file);
                var body = ok ? File.ReadAllBytes(file) : Encoding.ASCII.GetBytes("not found");
                var type = file.EndsWith(".json") ? "application/json" : "application/octet-stream";
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(head); stream.Write(body);
                Console.WriteLine($"{(ok ? 200 : 404)} /{rel}");
            }
        }
        default:
            return Fail("unknown command " + args[0]);
    }
}
catch (Exception ex) { return Fail(ex.Message); }
