using UnityEngine;
using Unity.Netcode;

/// <summary>
/// A NetworkBehaviour that also behaves as a singleton. 
/// T must be the same class inheriting this base.
/// </summary>
public class NetworkMonoBehaviourSingleton<T> : NetworkBehaviour where T : NetworkMonoBehaviourSingleton<T>
{
    private static T instance;
    public static T Instance
    {
        get
        {
            if (instance == null)
            {
                // Attempt to find an existing instance in the scene
                instance = FindObjectOfType<T>();
                if (instance == null)
                {
                    // If none is found, create a new one
                    var go = new GameObject(typeof(T).Name);
                    instance = go.AddComponent<T>();
                }
            }
            return instance;
        }
    }

    protected virtual void Awake()
    {
        // If there's already an instance and it's not this, destroy this one
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = (T)this;
    }
}
