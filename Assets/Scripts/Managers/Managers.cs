using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Managers : MonoBehaviour
{
    static Managers s_instance;
    static Managers Instance { get { Init(); return s_instance; } }

    DataManager _data = new DataManager();
    ResourceManager _resource = new ResourceManager();
    SceneManagerEx _scene = new SceneManagerEx();
    SoundManager _sound = new SoundManager();
    SpecDataManager _specData = new SpecDataManager();
    ConfigManager _config = new ConfigManager();
    UIManager _ui = new UIManager();
    URLManager _url = new URLManager();

    public static DataManager Data { get { return Instance._data; } }
    public static ResourceManager Resource { get { return Instance._resource; } }
    public static SceneManagerEx Scene { get { return Instance._scene; } }
    public static SoundManager Sound { get { return Instance._sound; } }
    public static SpecDataManager SpecData { get { return Instance._specData; } }
    public static ConfigManager Config { get { return Instance._config; } }
    public static UIManager UI { get { return Instance._ui; } }
    public static URLManager URL { get { return Instance._url; } }

    private void Start()
    {
        Init();
        StartCoroutine(CoInit());
    }

    public static void Init()
    {
        if (s_instance == null)
        {
            GameObject go = GameObject.Find("@Managers");
            if (go == null)
            {
                go = new GameObject { name = "@Managers" };
                go.AddComponent<Managers>();
            }

            DontDestroyOnLoad(go);
            s_instance = go.GetComponent<Managers>();

            s_instance._sound.Init();
            s_instance._resource.Init();
        }
    }

    public static void Clear()
    {
        Sound.Clear();
        UI.Clear();
    }

    // ══════════════════════════════════════════════════════════
    // 전체 데이터 초기화 코루틴 (SpecData → Config 순차 실행)
    // ══════════════════════════════════════════════════════════
    public IEnumerator CoInit()
    {
        yield return StartCoroutine(SpecData.CoDownloadDataSheet());
        yield return StartCoroutine(Config.CoDownloadConfig());

        OnAllDataReady();
    }

    public static bool IsDataReady { get; private set; }

    void OnAllDataReady()
    {
        IsDataReady = true;
        Debug.Log("[Managers] 모든 데이터 준비 완료. 게임 시작 가능.");

        // TODO - 데이터 로딩 끝나고 다른 씬으로 전환하기
        // 아래는 강제로 데이터 확인하는 테스트 코드
        Managers.UI.ShowSceneUI<UI_Test>();
    }
}