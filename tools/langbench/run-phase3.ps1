# Phase 3 comparison: what does each layer, and finally the local model, actually add?
# Sequential by design — the LanguageTool and llama.cpp servers each own a loopback port
# and share the CPU, so concurrent runs would make every latency column meaningless.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\langbench.exe'

$configs = @(
    @{ layers = 'rules,spell';                                   label = 'P3-1-deterministic' },
    @{ layers = 'rules,spell,rerank';                            label = 'P3-2-plus-reranker' },
    @{ layers = 'rules,spell,lt,rerank,lexveto,lexsignals';      label = 'P3-3-shipping-no-qwen' },
    @{ layers = 'rules,spell,lt,rerank,lexveto,lexsignals,ai';   label = 'P3-4-shipping-with-qwen' }
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
Write-Host 'phase 3 matrix complete'
