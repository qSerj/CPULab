"""Build a Windows bootstrap ZIP with a public key; never include private keys."""
import argparse
import hashlib
from pathlib import Path
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--public-key', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--offline-msi', type=Path)
args = parser.parse_args()
if args.offline_msi:
    expected = '788d8643464a01eb63640a2fd746f9ed23d106b5a901a35f88dc8cd40e446ac9'
    if hashlib.sha256(args.offline_msi.read_bytes()).hexdigest() != expected:
        parser.error('Expected the official OpenSSH-Win64-v10.4.0.0.msi SHA-256')
key = args.public_key.read_text().strip()
if not args.public_key.name.endswith('.pub') or not key.startswith('ssh-ed25519 '):
    parser.error('An Ed25519 PUBLIC .pub file is required')
source = Path(__file__).with_name('Setup.ps1').read_text()
# Keep the full command below cmd.exe's 8191 character line limit. The command
# contains the setup directly: no execution-policy change or file evaluation.
command = ' '.join(line.strip() for line in source.splitlines() if line.strip())
if any(character in command for character in ('"', '%', '\n', '\r')):
    raise ValueError('Setup source cannot be represented safely as a CMD command')
launcher = (
    '@echo off\r\nsetlocal DisableDelayedExpansion\r\n'
    'set "CPULAB_SETUP_ROOT=%~dp0"\r\n'
    '"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" '
    '-NoLogo -NoProfile -Command "& { ' + command + ' }"\r\n'
    'set "CPULAB_RESULT=%ERRORLEVEL%"\r\npause\r\nexit /b %CPULAB_RESULT%\r\n'
)
if max(map(len, launcher.splitlines())) > 7900:
    raise ValueError('CMD command is too long')
args.output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED) as archive:
    archive.writestr('CPUlab-SSH/SETUP-SSH.cmd', launcher.encode('ascii'))
    archive.writestr('CPUlab-SSH/client-key.pub', key + '\n')
    archive.writestr('CPUlab-SSH/Setup.ps1', source)
    archive.write(Path(__file__).with_name('README.txt'), 'CPUlab-SSH/README.txt')
    if args.offline_msi:
        archive.write(args.offline_msi, 'CPUlab-SSH/OpenSSH-Win64-v10.4.0.0.msi')
        archive.writestr('CPUlab-SSH/MSI-SOURCE.txt',
                        'Official Microsoft Win32-OpenSSH 10.4.0.0p1-Preview (non-production ready)\n'
                        'https://github.com/PowerShell/Win32-OpenSSH/releases/tag/10.4.0.0p1-Preview\n'
                        'SHA-256: ' + expected + '\n'
                        'Unmodified installer; upstream license notices are included in the distribution.\n')
print(args.output.resolve())
