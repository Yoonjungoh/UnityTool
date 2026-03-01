using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SampleScene : BaseScene
{

    protected override void Init()
    {
        Managers.Init();
    }

    private void Awake()
    {
        Init();
    }
}
