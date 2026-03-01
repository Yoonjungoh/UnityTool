using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UI_Test : UI_Scene
{
    Button _testButton;
    TextMeshProUGUI _testText;
    int _score = 0;
    enum Buttons
    {
        TestButton,
    }
    enum Texts
    {
        TestText,
    }

    public override void Init()
    {
        base.Init();
        Bind<Button>(typeof(Buttons));
        Bind<TextMeshProUGUI>(typeof(Texts));
        _testButton = GetButton((int)Buttons.TestButton);
        _testText = GetTextMeshProUGUI((int)Texts.TestText);
        _testButton.onClick.AddListener(AddScore);
        _testText.text = Managers.SpecData.GetCurrency(1).CurrencyType.ToString();
    }

    void AddScore()
    {
        _score++;
        _testText.text = Managers.Config.GetString(ConfigType.TestWord);
        // Managers.Scene.LoadScene("Login");
    }
}
