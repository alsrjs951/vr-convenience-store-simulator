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
    # Day 3: the first time each special request comes up (game design 10.3).
    "voice_guide_microwave"   = "데워 달라는 손님이에요. 스캔한 다음 오른쪽 벽 전자레인지에 넣고, 땡 소리가 나면 꺼내 주세요."
    "voice_guide_hotwater"    = "뜨거운 물을 부어 달라는 손님이에요. 스캔한 다음 오른쪽 벽 온수기 탭 아래에 잠깐 대 주세요."
    "voice_guide_oneplus"     = "원 플러스 원 행사예요. 두 개 모두 스캔하면 한 개 값만 계산돼요."
    # Customers at the end of an order, and the store manager on the way in and out (game design 10.0, 11).
    "voice_thanks"            = "감사합니다."
    "voice_thanks_fast"       = "와, 빠르시네요. 감사합니다."
    "voice_manager_welcome"   = "어서 와요, 점장이에요. 화살표를 따라 계산대 안쪽으로 들어오세요."
    "voice_manager_punch_in"  = "왔어요? 계산대 위 출근 카드를 집어서, 단말기에 찍어 주세요."
    "voice_manager_punch_out" = "수고 많았어요. 퇴근 카드 찍고 들어가세요."
    "voice_ending_s"          = "정말 훌륭했어요. 다음 달부터 정식으로 같이 일해 볼래요?"
    "voice_ending_a"          = "아주 잘했어요. 다음 주 근무표에도 이름 올려 둘게요."
    "voice_ending_b"          = "수고했어요. 조금만 더 손에 익히면 금방 좋아질 거예요."
    "voice_ending_c"          = "첫 알바는 원래 어려워요. 다음에 다시 한번 해 봐요."
}

foreach ($name in $lines.Keys) {
    $synth.SetOutputToWaveFile((Join-Path $OutDir "$name.wav"), $format)
    $synth.Speak($lines[$name])
}
$synth.SetOutputToNull()
$synth.Dispose()
"wrote $($lines.Count) lines to $OutDir"
