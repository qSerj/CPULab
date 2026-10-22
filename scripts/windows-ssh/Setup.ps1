$ErrorActionPreference = 'Stop';
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent();
    $principal = New-Object Security.Principal.WindowsPrincipal($identity);
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Right-click SETUP-SSH.cmd and choose Run as administrator.'; };
    $keyPath = Join-Path $env:CPULAB_SETUP_ROOT 'client-key.pub';
    $key = (Get-Content -LiteralPath $keyPath -Raw).Trim();
    if ($key -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+(?: .*)?$') { throw 'Missing or invalid client-key.pub'; };
    Write-Host 'CPU Lab: setting up SSH access from 192.168.143.0/24. No benchmarks.';
    $msi = Join-Path $env:CPULAB_SETUP_ROOT 'OpenSSH-Win64-v10.4.0.0.msi';
    if (!(Get-Service sshd -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $msi)) {
        Write-Host 'Installing official Win32-OpenSSH 10.4.0.0p1-Preview offline (non-production preview).';
        if ([Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine') -ne 'AMD64') { throw 'This offline MSI requires x64 Windows.'; };
        if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne '788d8643464a01eb63640a2fd746f9ed23d106b5a901a35f88dc8cd40e446ac9') { throw 'Offline MSI SHA-256 mismatch.'; };
        $signature = Get-AuthenticodeSignature -LiteralPath $msi;
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(?:,|$)') { throw 'Offline MSI Microsoft Authenticode signature could not be validated. No installation attempted.'; };
        $log = Join-Path $env:CPULAB_SETUP_ROOT 'openssh-install.log';
        $arguments = '/i ' + [char]34 + $msi + [char]34 + ' ADDLOCAL=Server /qn /norestart /L*v ' + [char]34 + $log + [char]34;
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -Wait -PassThru;
        if ($process.ExitCode -eq 3010) { throw 'MSI requests a restart. Restart manually, then run setup again.'; };
        if ($process.ExitCode -ne 0) { throw ('MSI failed: ' + $process.ExitCode + '. See openssh-install.log.'); };
        if (!(Get-Service sshd -ErrorAction SilentlyContinue)) { throw 'MSI did not register sshd.'; };
    };
    if (!(Get-Service sshd -ErrorAction SilentlyContinue)) {
        $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0';
        $fod = Join-Path $env:CPULAB_SETUP_ROOT 'FoD';
        if (Test-Path -LiteralPath $fod) {
            $installed = Add-WindowsCapability -Online -Name $capability.Name -Source $fod -LimitAccess;
        } else {
            Write-Host 'Installing the Windows OpenSSH Server component (Windows Update may need internet).';
            $installed = Add-WindowsCapability -Online -Name $capability.Name;
        };
        if ($installed.RestartNeeded) { throw 'Windows requests a restart. Restart manually, then run this setup again.'; };
    };
    $sshDirectory = Join-Path $env:ProgramData 'ssh';
    New-Item -ItemType Directory -Path $sshDirectory -Force | Out-Null;
    $authorized = Join-Path $sshDirectory 'administrators_authorized_keys';
    if (!(Test-Path -LiteralPath $authorized)) { [IO.File]::WriteAllText($authorized, ''); };
    $content = [IO.File]::ReadAllText($authorized);
    $keyMaterial = ($key -split ' ')[1];
    if ($content -notmatch [regex]::Escape($keyMaterial)) {
        [IO.File]::AppendAllText($authorized, [Environment]::NewLine + $key + [Environment]::NewLine, [Text.Encoding]::ASCII);
    };
    $acl = New-Object Security.AccessControl.FileSecurity;
    $acl.SetAccessRuleProtection($true, $false);
    foreach ($sidText in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidText);
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow');
        $acl.AddAccessRule($rule);
    };
    Set-Acl -LiteralPath $authorized -AclObject $acl;
    $ruleName = 'CPUlab-SSH-LAN';
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -Name $ruleName;
    };
    New-NetFirewallRule -Name $ruleName -DisplayName 'CPU Lab SSH LAN' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -RemoteAddress '192.168.143.0/24' -Profile Any | Out-Null;
    Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Disable-NetFirewallRule;
    Set-Service -Name sshd -StartupType Automatic;
    Start-Service sshd;
    $ips = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.143.*' } | Select-Object -ExpandProperty IPAddress);
    $service = Get-CimInstance Win32_Service -Filter 'Name = ''sshd''';
    $binary = $service.PathName.TrimStart([char]34);
    $binary = $binary.Substring(0, $binary.IndexOf('sshd.exe', [StringComparison]::OrdinalIgnoreCase) + 8);
    $fingerprint = & (Join-Path (Split-Path -Parent $binary) 'ssh-keygen.exe') -lf (Join-Path $sshDirectory 'ssh_host_ed25519_key.pub');
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read SSH host fingerprint.'; };
    $details = @('SSH setup finished. Connection is not verified yet.', ('Account: ' + $identity.Name), ('IPv4: ' + ($ips -join ', ')), ('Host key: ' + $fingerprint), 'Send this file to the CPU Lab chat. It contains no password or private key.');
    $details | Set-Content -LiteralPath (Join-Path $env:CPULAB_SETUP_ROOT 'connection.txt') -Encoding UTF8;
    $details | ForEach-Object { Write-Host $_; };
    Write-Host 'Existing custom sshd_config or firewall rules may need separate inspection.';
} catch {
    try { ('SETUP FAILED: ' + $_.Exception.Message) | Set-Content -LiteralPath (Join-Path $env:CPULAB_SETUP_ROOT 'setup-error.txt') -Encoding UTF8; } catch {};
    Write-Host ('SETUP FAILED: ' + $_.Exception.Message) -ForegroundColor Red;
    Write-Host 'No security policy bypass is attempted. Already completed setup steps may remain.';
    exit 1;
};
