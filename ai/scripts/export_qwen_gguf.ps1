[CmdletBinding()]
param(
    [string]$AdapterDir = "ai/outputs/qwen_smoke/adapter",
    [string]$BaseModel = "ai/models/Qwen2.5-0.5B-Instruct",
    [string]$OutputDir = "models/writelight-qwen",
    [string]$LlamaDirectory = "artifacts/llama-bin",
    [ValidateSet("Q4_K_M", "Q5_K_M", "Q8_0")]
    [string]$Quantization = "Q4_K_M"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$python = (Get-Command python).Source
& $python (Join-Path $root 'ai/scripts/export_writelight_qwen.py') --adapter-dir (Join-Path $root $AdapterDir) --base-model (Join-Path $root $BaseModel) --output-dir (Join-Path $root $OutputDir) --merge
if ($LASTEXITCODE -ne 0) { throw "HF/LoRA merge failed ($LASTEXITCODE)" }

$converter = Get-ChildItem (Join-Path $root 'artifacts') -Recurse -Filter convert_hf_to_gguf.py | Select-Object -First 1 -ExpandProperty FullName
$quantize = Join-Path $root "$LlamaDirectory/llama-quantize.exe"
if (-not $converter -or -not (Test-Path $quantize)) { throw 'llama.cpp converter/quantizer is required for GGUF export.' }
$f16 = Join-Path $root 'artifacts/writelight-qwen-f16.gguf'
$gguf = Join-Path $root "$OutputDir/writelight-qwen-$($Quantization.ToLowerInvariant()).gguf"
& $python $converter (Join-Path $root "$OutputDir/merged-hf") --outfile $f16 --outtype f16
if ($LASTEXITCODE -ne 0) { throw "HF to GGUF conversion failed ($LASTEXITCODE)" }
& $quantize $f16 $gguf $Quantization
if ($LASTEXITCODE -ne 0) { throw "GGUF quantization failed ($LASTEXITCODE)" }
Write-Host "GGUF ready: $gguf"
