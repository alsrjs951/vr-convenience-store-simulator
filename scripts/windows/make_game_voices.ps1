# Writes the game's spoken lines with the Windows Korean TTS voice (Microsoft Heami, built into Korean Windows).
# Output: one 22.05 kHz mono WAV per line in $OutDir. make_game_sounds.py then trims, normalizes and makes male versions.
# Usage: powershell -File scripts/windows/make_game_voices.ps1 -OutDir <tts_raw_dir>
param([Parameter(Mandatory = $true)][string]$OutDir)

Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice("Microsoft Heami Desktop")
$synth.Rate = 0
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

# Customer lines (implementation doc 6.1: greeting 2, hurry 1, leaving 1, requests 4 = bag, microwave, hot water, 1+1).
$lines = [ordered]@{
    "voice_greet_1"           = "안녕하세요."
    "voice_greet_2"           = "이거 계산해 주세요."
    "voice_bag"               = "봉투에 담아 주세요."
    "voice_hurry"             = "저기요, 조금만 서둘러 주시겠어요?"
    "voice_leave"             = "너무 오래 걸리네요. 그냥 갈게요."
    "voice_special_microwave" = "이거 전자레인지에 데워 주세요."
    "voice_special_hotwater"  = "컵라면에 뜨거운 물 좀 부어 주세요."
    "voice_special_oneplus"   = "이거 원 플러스 원 맞죠?"
    # Day 1 tutorial narration, T1 to T6 (doc 5.6).
    "voice_tutorial_1"        = "계산대 위의 상품에 손을 대고, 컨트롤러 옆의 그립 버튼을 눌러 잡아 보세요."
    "voice_tutorial_2"        = "손목을 돌려서 바코드가 있는 면을 찾아, 스캐너 쪽으로 향하게 하세요."
    "voice_tutorial_3"        = "바코드 면을 스캐너 유리에 대 보세요. 삑 소리가 나면 성공이에요."
    "voice_tutorial_4"        = "스캔한 상품을 비닐봉투 위로 옮긴 다음, 손을 놓으세요."
    "voice_tutorial_5"        = "손님 카드를 집어서, 오른쪽 단말기 앞면의 노란 칸에 밀어 넣으세요."
    "voice_tutorial_6"        = "손님이 낸 오천 원을 왼쪽 서랍에 넣고, 거스름돈 삼천오백 원을 접시에 놓으세요."
    # Day 2 intake narration, I1 to I4 (doc 5.7.4).
    "voice_intake_1"          = "뒤쪽 창고 입구의 카트에서 첫 번째 박스를 옆면 손잡이로 들고, 노란 칸이 있는 진열대 옆 거치대에 올려 주세요."
    "voice_intake_2"          = "거치대 앞의 커터를 집어서, 박스 위 테이프를 한쪽 끝에서 다른 쪽 끝까지 그어 주세요."
    "voice_intake_3"          = "박스 날개를 하나씩 잡고, 바깥쪽으로 끝까지 젖혀 주세요."
    "voice_intake_4"          = "상품을 하나 꺼내 들고 노란 칸을 바라보세요. 초록색 미리보기가 뜨면 손을 놓으면 돼요."
}

foreach ($name in $lines.Keys) {
    $synth.SetOutputToWaveFile((Join-Path $OutDir "$name.wav"), $format)
    $synth.Speak($lines[$name])
}
$synth.SetOutputToNull()
$synth.Dispose()
"wrote $($lines.Count) lines to $OutDir"
