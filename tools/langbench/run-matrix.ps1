# Runs the layer-attribution matrix. Sequential by design: the LanguageTool and
# llama.cpp runs each own a loopback port, and concurrent runs would contend for
# those and for the CPU, making the latency columns meaningless.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\langbench.exe'

$configs = @(
    @{ layers = 'rules';                 label = '01-rules' },
    @{ layers = 'spell';                 label = '02-spell' },
    @{ layers = 'lt';                    label = '03-lt' },
    @{ layers = 'rules,spell';           label = '04-rules-spell' },
    @{ layers = 'rules,spell,lt';        label = '05-rules-spell-lt' },
    @{ layers = 'rules,spell,lt,ai';     label = '06-full-pipeline' }
)

foreach ($c in $configs) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host "RUN $($c.label)  [$($c.layers)]"
    Write-Host ('=' * 78)
    & $exe --layers $c.layers --label $c.label
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $($c.label) exit=$LASTEXITCODE" }
}

Write-Host ''
Write-Host 'matrix complete'
