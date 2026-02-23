using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Util
{
    private static readonly Dictionary<GameObject, Dictionary<Type, Dictionary<string, UnityEngine.Object>>> _cache = new Dictionary<GameObject, Dictionary<Type, Dictionary<string, UnityEngine.Object>>>();

    public static T FindChild<T>(GameObject root, string name, bool recursive = false) where T : UnityEngine.Object
    {
        if (root == null || string.IsNullOrEmpty(name))
            return null;

        // 캐시 컨테이너 확보
        if (_cache.TryGetValue(root, out var typeMap) == false)
        {
            typeMap = new Dictionary<Type, Dictionary<string, UnityEngine.Object>>();
            _cache[root] = typeMap;
        }

        // 타입별 캐시
        if (typeMap.TryGetValue(typeof(T), out var nameMap) == false)
        {
            nameMap = new Dictionary<string, UnityEngine.Object>();
            typeMap[typeof(T)] = nameMap;
        }

        // 캐시 히트
        if (nameMap.TryGetValue(name, out var cached))
        {
            return cached as T;
        }

        // 캐시 미스면 실제 탐색 진행
        T found = recursive ? FindRecursive<T>(root, name) : FindDirect<T>(root, name);

        if (found != null)
        {
            nameMap[name] = found;
        }

        return found;
    }

    private static T FindDirect<T>(GameObject root, string name) where T : UnityEngine.Object
    {
        Transform parent = root.transform;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name != name)
                continue;

            if (typeof(T) == typeof(Transform))
                return child as T;

            return child.GetComponent<T>();
        }
        return null;
    }

    private static T FindRecursive<T>(GameObject root, string name) where T : UnityEngine.Object
    {
        foreach (Transform tr in root.GetComponentsInChildren<Transform>(true))
        {
            if (tr.name != name)
                continue;

            if (typeof(T) == typeof(Transform))
                return tr as T;

            return tr.GetComponent<T>();
        }
        return null;
    }

    public static void ClearCache(GameObject root)
    {
        if (root != null)
        {
            _cache.Remove(root);
        }
    }

    public static long GetTimestampMs()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    public static T GetOrAddComponent<T>(GameObject go) where T : UnityEngine.Component
    {
        T component = go.GetComponent<T>();
        if (component == null)
            component = go.AddComponent<T>();
        return component;
    }

    public static GameObject FindChild(GameObject go, string name = null, bool recursive = false)
    {
        Transform transform = FindChild<Transform>(go, name, recursive);
        if (transform == null)
            return null;

        return transform.gameObject;
    }
}
