namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ce que la borne affiche au joueur pendant un challenge.
///
/// La langue se resout par couches, et l'ordre compte :
///
///   1. le joueur, quand il est identifie - sa session porte sa langue ;
///   2. la borne, sinon - c'est la langue de la salle, celle qu'un passant
///      comprend le mieux avant de s'etre connecte ;
///   3. l'anglais en dernier recours.
///
/// La borne elle-meme ne change pas de langue quand un joueur arrive :
/// basculer EmulationStation a chaque check-in serait lourd et lent. Seules
/// ces chaines suivent le joueur.
/// </summary>
public static class CabinetAnnounceText
{
    /// <summary>Les langues servies. Toute autre valeur retombe sur l'anglais.</summary>
    private static readonly string[] Supported = ["en", "fr", "es", "ja", "zh", "ko"];

    /// <summary>
    /// Ramene un code quelconque - « fr », « fr_FR », « fr-FR », « FRANCAIS » -
    /// a l'une des six langues servies.
    /// </summary>
    public static string Normalize(string? value)
    {
        var code = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length == 0)
        {
            return string.Empty;
        }

        // « fr_FR » et « fr-FR » comptent comme « fr ».
        var cut = code.IndexOfAny(['_', '-']);
        if (cut > 0)
        {
            code = code[..cut];
        }

        return Array.IndexOf(Supported, code) >= 0 ? code : string.Empty;
    }

    /// <summary>
    /// La langue a employer, du joueur vers la borne vers l'anglais. Le premier
    /// niveau reconnu gagne : un joueur japonais sur une borne francaise lit du
    /// japonais, un passant anonyme lit du francais.
    /// </summary>
    public static string Resolve(string? playerLocale, string? cabinetLocale)
    {
        var player = Normalize(playerLocale);
        if (player.Length > 0)
        {
            return player;
        }

        var cabinet = Normalize(cabinetLocale);
        return cabinet.Length > 0 ? cabinet : "en";
    }

    /// <summary>La chaine si la table la connait (dans cette langue ou en anglais), sinon null.</summary>
    public static string? Find(string key, string locale)
    {
        var texte = Get(key, locale);
        return string.Equals(texte, key, StringComparison.Ordinal) ? null : texte;
    }

    /// <summary>Une chaine d'annonce dans la langue resolue.</summary>
    public static string Get(string key, string locale)
    {
        var code = Normalize(locale);
        if (code.Length == 0)
        {
            code = "en";
        }

        if (Words.TryGetValue(code, out var table) && table.TryGetValue(key, out var text))
        {
            return text;
        }

        // Repli cle par cle : une chaine ajoutee au francais et pas encore
        // traduite s'affiche en anglais plutot que de disparaitre.
        return Words["en"].TryGetValue(key, out var fallback) ? fallback : key;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Words = new()
    {
        ["fr"] = new()
        {
            ["start_title"] = "Appuyez sur START pour commencer la partie",
            ["start_sub"] = "Elle sera mise en pause automatiquement - prête pour le départ",
            ["hold_title"] = "Ne touchez plus à rien !",
            ["hold_sub"] = "Partie en pause - départ imminent, attendez le décompte",
            ["reached_title"] = "🏁 Objectif atteint !",
            ["reached_sub"] = "Votre temps est enregistré - regardez le classement !",
            ["end_title"] = "🏁 Challenge terminé !",
            ["end_sub"] = "Classement sur l'écran de la salle - merci d'avoir joué !",
            ["countdown"] = "Départ dans…",
            ["go"] = "GO !",
            ["ready"] = "Tenez-vous prêt !",
            ["launching"] = "Le jeu se lance…",
            ["scan"] = "📱 Scannez pour participer - vos scores à votre nom",
            ["open_to_all"] = "Ouvert à tous",
            ["scoring_certifiable"] = "Partie certifiable",
            ["scoring_for_ranking"] = "pour le classement",
            ["scoring_forced"] = "réglages certifiés appliqués",
            ["scoring_not_certifiable"] = "Partie non certifiable",
            ["scoring_frontend_off"] = "{0}, à désactiver dans les options RetroBat de ce jeu",
            ["scoring_danger_rewind"] = "rembobinage (Rewind)",
            ["scoring_danger_runahead"] = "run-ahead",
            ["scoring_danger_autosave"] = "sauvegarde d'état automatique",
            ["scoring_emulator_pending"] = "Émulateur pas encore reconnu",
            ["scoring_emulator_pending_sub"] = "ton score sera gardé et classé dès qu'il le sera",
            ["scoring_settings_pending"] = "Réglages en attente",
            ["scoring_settings_pending_sub"] = "ton score sera gardé et classé dès qu'ils seront reconnus",
            ["scoring_nothing_measured"] = "Aucun score ne sera mesuré",
            ["scoring_module_missing"] = "le module de mesure est absent de cette borne",
            ["scoring_module_idle"] = "le module de mesure n'enveloppe aucun émulateur",
            ["scoring_core_blind"] = "le cœur « {0} » n'expose pas sa mémoire",
            ["scoring_none_measured"] = "Aucun score n'a été mesuré",
            ["scoring_none_measured_sub"] = "la partie annoncée certifiable n'a rien remonté",
            ["reason_profile_content_mismatch"] = "ROM non reconnue",
            ["reason_profile_mem_mismatch"] = "définition mémoire non reconnue",
            ["reason_profile_listener_unauthorized"] = "listener non homologué (wrapper ou plugin MAME)",
            ["reason_profile_not_open"] = "classement fermé",
            ["reason_profile_nvram_mismatch"] = "réglages internes du jeu différents de ceux d'usine",
            ["reason_profile_bios_mismatch"] = "BIOS non reconnu",
            ["reason_profile_mismatch"] = "jeu ou règlement non concordant",
            ["reason_runtime_module_unauthorized"] = "logiciel non homologué",
        },
        ["en"] = new()
        {
            ["start_title"] = "Press START to begin",
            ["start_sub"] = "It will pause on its own - ready for the countdown",
            ["hold_title"] = "Hands off!",
            ["hold_sub"] = "Paused - the start is close, wait for the countdown",
            ["reached_title"] = "🏁 Target reached!",
            ["reached_sub"] = "Your time is recorded - watch the leaderboard!",
            ["end_title"] = "🏁 Challenge over!",
            ["end_sub"] = "Leaderboard on the venue screen - thanks for playing!",
            ["countdown"] = "Starting in…",
            ["go"] = "GO!",
            ["ready"] = "Get ready!",
            ["launching"] = "Launching the game…",
            ["scan"] = "📱 Scan to join - your scores under your name",
            ["open_to_all"] = "Open to all",
            ["scoring_certifiable"] = "Certifiable run",
            ["scoring_for_ranking"] = "for the leaderboard",
            ["scoring_forced"] = "certified settings applied",
            ["scoring_not_certifiable"] = "Run not certifiable",
            ["scoring_frontend_off"] = "{0}: turn it off in this game's RetroBat options",
            ["scoring_danger_rewind"] = "rewind",
            ["scoring_danger_runahead"] = "run-ahead",
            ["scoring_danger_autosave"] = "automatic save state",
            ["scoring_emulator_pending"] = "Emulator not recognized yet",
            ["scoring_emulator_pending_sub"] = "your score will be kept and ranked as soon as it is",
            ["scoring_settings_pending"] = "Settings pending",
            ["scoring_settings_pending_sub"] = "your score will be kept and ranked as soon as they are recognized",
            ["scoring_nothing_measured"] = "No score will be measured",
            ["scoring_module_missing"] = "the measurement module is missing on this cabinet",
            ["scoring_module_idle"] = "the measurement module wraps no emulator",
            ["scoring_core_blind"] = "the “{0}” core does not expose its memory",
            ["scoring_none_measured"] = "No score was measured",
            ["scoring_none_measured_sub"] = "the run announced as certifiable reported nothing",
            ["reason_profile_content_mismatch"] = "ROM not recognized",
            ["reason_profile_mem_mismatch"] = "memory definition not recognized",
            ["reason_profile_listener_unauthorized"] = "measurement module not approved (wrapper or MAME plugin)",
            ["reason_profile_not_open"] = "leaderboard closed",
            ["reason_profile_nvram_mismatch"] = "the game's internal settings differ from factory settings",
            ["reason_profile_bios_mismatch"] = "BIOS not recognized",
            ["reason_profile_mismatch"] = "game or ruleset mismatch",
            ["reason_runtime_module_unauthorized"] = "unapproved software",
        },
        ["es"] = new()
        {
            ["start_title"] = "Pulsa START para empezar la partida",
            ["start_sub"] = "Se pausará sola - lista para la salida",
            ["hold_title"] = "¡No toques nada!",
            ["hold_sub"] = "En pausa - la salida es inminente, espera la cuenta atrás",
            ["reached_title"] = "🏁 ¡Objetivo alcanzado!",
            ["reached_sub"] = "Tu tiempo queda registrado - ¡mira la clasificación!",
            ["end_title"] = "🏁 ¡Reto terminado!",
            ["end_sub"] = "Clasificación en la pantalla de la sala - ¡gracias por jugar!",
            ["countdown"] = "Salida en…",
            ["go"] = "¡YA!",
            ["ready"] = "¡Prepárate!",
            ["launching"] = "Lanzando el juego…",
            ["scan"] = "📱 Escanea para participar - tus puntuaciones a tu nombre",
            ["open_to_all"] = "Abierto a todos",
            ["scoring_certifiable"] = "Partida certificable",
            ["scoring_for_ranking"] = "para la clasificación",
            ["scoring_forced"] = "ajustes certificados aplicados",
            ["scoring_not_certifiable"] = "Partida no certificable",
            ["scoring_frontend_off"] = "{0}: desactívalo en las opciones de RetroBat de este juego",
            ["scoring_danger_rewind"] = "rebobinado (Rewind)",
            ["scoring_danger_runahead"] = "run-ahead",
            ["scoring_danger_autosave"] = "guardado de estado automático",
            ["scoring_emulator_pending"] = "Emulador aún no reconocido",
            ["scoring_emulator_pending_sub"] = "tu puntuación se guardará y se clasificará en cuanto lo sea",
            ["scoring_settings_pending"] = "Ajustes pendientes",
            ["scoring_settings_pending_sub"] = "tu puntuación se guardará y se clasificará en cuanto se reconozcan",
            ["scoring_nothing_measured"] = "No se medirá ninguna puntuación",
            ["scoring_module_missing"] = "falta el módulo de medición en esta máquina",
            ["scoring_module_idle"] = "el módulo de medición no envuelve ningún emulador",
            ["scoring_core_blind"] = "el núcleo «{0}» no expone su memoria",
            ["scoring_none_measured"] = "No se ha medido ninguna puntuación",
            ["scoring_none_measured_sub"] = "la partida anunciada como certificable no ha transmitido nada",
            ["reason_profile_content_mismatch"] = "ROM no reconocida",
            ["reason_profile_mem_mismatch"] = "definición de memoria no reconocida",
            ["reason_profile_listener_unauthorized"] = "módulo de medición no homologado (wrapper o plugin de MAME)",
            ["reason_profile_not_open"] = "clasificación cerrada",
            ["reason_profile_nvram_mismatch"] = "los ajustes internos del juego difieren de los de fábrica",
            ["reason_profile_bios_mismatch"] = "BIOS no reconocida",
            ["reason_profile_mismatch"] = "juego o reglamento no coincidente",
            ["reason_runtime_module_unauthorized"] = "software no homologado",
        },
        ["ja"] = new()
        {
            ["start_title"] = "START を押してプレイ開始",
            ["start_sub"] = "自動で一時停止します - スタートの準備へ",
            ["hold_title"] = "そのままお待ちください",
            ["hold_sub"] = "一時停止中 - まもなくスタート、カウントをお待ちください",
            ["reached_title"] = "🏁 目標達成！",
            ["reached_sub"] = "タイムを記録しました - ランキングをご覧ください",
            ["end_title"] = "🏁 チャレンジ終了！",
            ["end_sub"] = "順位は会場の画面に - ご参加ありがとうございました",
            ["countdown"] = "スタートまで…",
            ["go"] = "GO！",
            ["ready"] = "ご準備ください",
            ["launching"] = "ゲームを起動しています…",
            ["scan"] = "📱 スキャンして参加 - スコアはあなたの名前で",
            ["open_to_all"] = "どなたでも参加できます",
            ["scoring_certifiable"] = "認定対象のプレイ",
            ["scoring_for_ranking"] = "ランキングに反映されます",
            ["scoring_forced"] = "認定設定を適用しました",
            ["scoring_not_certifiable"] = "認定対象外のプレイ",
            ["scoring_frontend_off"] = "{0}：このゲームの RetroBat オプションで無効にしてください",
            ["scoring_danger_rewind"] = "巻き戻し（Rewind）",
            ["scoring_danger_runahead"] = "ランアヘッド",
            ["scoring_danger_autosave"] = "自動ステートセーブ",
            ["scoring_emulator_pending"] = "エミュレーターは未認識です",
            ["scoring_emulator_pending_sub"] = "認識され次第、スコアは保存されランキングに反映されます",
            ["scoring_settings_pending"] = "設定の確認待ち",
            ["scoring_settings_pending_sub"] = "設定が認識され次第、スコアは保存されランキングに反映されます",
            ["scoring_nothing_measured"] = "スコアは計測されません",
            ["scoring_module_missing"] = "この筐体には計測モジュールがありません",
            ["scoring_module_idle"] = "計測モジュールがどのエミュレーターにも適用されていません",
            ["scoring_core_blind"] = "コア「{0}」はメモリを公開していません",
            ["scoring_none_measured"] = "スコアは計測されませんでした",
            ["scoring_none_measured_sub"] = "認定対象と表示されたプレイから何も届きませんでした",
            ["reason_profile_content_mismatch"] = "ROM が認識されません",
            ["reason_profile_mem_mismatch"] = "メモリ定義が認識されません",
            ["reason_profile_listener_unauthorized"] = "計測モジュールが未承認です（wrapper または MAME プラグイン）",
            ["reason_profile_not_open"] = "ランキングは終了しています",
            ["reason_profile_nvram_mismatch"] = "ゲーム内部の設定が工場出荷時と異なります",
            ["reason_profile_bios_mismatch"] = "BIOS が認識されません",
            ["reason_profile_mismatch"] = "ゲームまたはルールが一致しません",
            ["reason_runtime_module_unauthorized"] = "未承認のソフトウェア",
        },
        ["zh"] = new()
        {
            ["start_title"] = "按 START 开始游戏",
            ["start_sub"] = "它会自动暂停 - 等待发车",
            ["hold_title"] = "请勿操作！",
            ["hold_sub"] = "已暂停 - 马上开始，请等待倒数",
            ["reached_title"] = "🏁 达成目标！",
            ["reached_sub"] = "你的成绩已记录 - 看看排行榜！",
            ["end_title"] = "🏁 挑战结束！",
            ["end_sub"] = "排名显示在场馆屏幕上 - 感谢参与！",
            ["countdown"] = "倒数…",
            ["go"] = "GO！",
            ["ready"] = "请准备！",
            ["launching"] = "正在启动游戏…",
            ["scan"] = "📱 扫码参与 - 成绩记在你的名下",
            ["open_to_all"] = "所有人皆可参加",
            ["scoring_certifiable"] = "可认证的对局",
            ["scoring_for_ranking"] = "计入排行榜",
            ["scoring_forced"] = "已应用认证设置",
            ["scoring_not_certifiable"] = "不可认证的对局",
            ["scoring_frontend_off"] = "{0}：请在此游戏的 RetroBat 选项中关闭",
            ["scoring_danger_rewind"] = "倒带（Rewind）",
            ["scoring_danger_runahead"] = "预运行（run-ahead）",
            ["scoring_danger_autosave"] = "自动即时存档",
            ["scoring_emulator_pending"] = "模拟器尚未被识别",
            ["scoring_emulator_pending_sub"] = "识别后，你的分数将被保留并计入排行榜",
            ["scoring_settings_pending"] = "设置待确认",
            ["scoring_settings_pending_sub"] = "设置被识别后，你的分数将被保留并计入排行榜",
            ["scoring_nothing_measured"] = "不会测量任何分数",
            ["scoring_module_missing"] = "此机台缺少测量模块",
            ["scoring_module_idle"] = "测量模块未挂接任何模拟器",
            ["scoring_core_blind"] = "核心“{0}”不公开其内存",
            ["scoring_none_measured"] = "未测量到任何分数",
            ["scoring_none_measured_sub"] = "被标记为可认证的对局没有上报任何数据",
            ["reason_profile_content_mismatch"] = "ROM 未被识别",
            ["reason_profile_mem_mismatch"] = "内存定义未被识别",
            ["reason_profile_listener_unauthorized"] = "测量模块未经认可（wrapper 或 MAME 插件）",
            ["reason_profile_not_open"] = "排行榜已关闭",
            ["reason_profile_nvram_mismatch"] = "游戏内部设置与出厂设置不同",
            ["reason_profile_bios_mismatch"] = "BIOS 未被识别",
            ["reason_profile_mismatch"] = "游戏或规则不一致",
            ["reason_runtime_module_unauthorized"] = "未经认可的软件",
        },
        ["ko"] = new()
        {
            ["start_title"] = "START를 눌러 시작하세요",
            ["start_sub"] = "자동으로 일시정지됩니다 - 출발 준비",
            ["hold_title"] = "손을 떼 주세요!",
            ["hold_sub"] = "일시정지 - 곧 시작합니다, 카운트다운을 기다려 주세요",
            ["reached_title"] = "🏁 목표 달성!",
            ["reached_sub"] = "기록이 저장되었습니다 - 순위표를 확인하세요!",
            ["end_title"] = "🏁 챌린지 종료!",
            ["end_sub"] = "순위는 매장 화면에 - 참여해 주셔서 감사합니다!",
            ["countdown"] = "시작까지…",
            ["go"] = "GO!",
            ["ready"] = "준비하세요!",
            ["launching"] = "게임을 실행하는 중…",
            ["scan"] = "📱 스캔해서 참여 - 점수는 당신의 이름으로",
            ["open_to_all"] = "누구나 참여 가능",
            ["scoring_certifiable"] = "인증 가능한 플레이",
            ["scoring_for_ranking"] = "랭킹에 반영됩니다",
            ["scoring_forced"] = "인증 설정이 적용되었습니다",
            ["scoring_not_certifiable"] = "인증할 수 없는 플레이",
            ["scoring_frontend_off"] = "{0}: 이 게임의 RetroBat 옵션에서 끄세요",
            ["scoring_danger_rewind"] = "되감기(Rewind)",
            ["scoring_danger_runahead"] = "런어헤드",
            ["scoring_danger_autosave"] = "자동 상태 저장",
            ["scoring_emulator_pending"] = "아직 인식되지 않은 에뮬레이터",
            ["scoring_emulator_pending_sub"] = "인식되는 즉시 점수가 보관되고 랭킹에 반영됩니다",
            ["scoring_settings_pending"] = "설정 확인 대기 중",
            ["scoring_settings_pending_sub"] = "설정이 인식되는 즉시 점수가 보관되고 랭킹에 반영됩니다",
            ["scoring_nothing_measured"] = "점수가 측정되지 않습니다",
            ["scoring_module_missing"] = "이 기기에는 측정 모듈이 없습니다",
            ["scoring_module_idle"] = "측정 모듈이 어떤 에뮬레이터에도 연결되어 있지 않습니다",
            ["scoring_core_blind"] = "코어 “{0}”은(는) 메모리를 공개하지 않습니다",
            ["scoring_none_measured"] = "측정된 점수가 없습니다",
            ["scoring_none_measured_sub"] = "인증 가능으로 안내된 플레이에서 아무것도 전달되지 않았습니다",
            ["reason_profile_content_mismatch"] = "ROM이 인식되지 않습니다",
            ["reason_profile_mem_mismatch"] = "메모리 정의가 인식되지 않습니다",
            ["reason_profile_listener_unauthorized"] = "측정 모듈이 승인되지 않았습니다(wrapper 또는 MAME 플러그인)",
            ["reason_profile_not_open"] = "랭킹이 닫혀 있습니다",
            ["reason_profile_nvram_mismatch"] = "게임 내부 설정이 공장 초기값과 다릅니다",
            ["reason_profile_bios_mismatch"] = "BIOS가 인식되지 않습니다",
            ["reason_profile_mismatch"] = "게임 또는 규칙이 일치하지 않습니다",
            ["reason_runtime_module_unauthorized"] = "승인되지 않은 소프트웨어",
        },
    };
}
