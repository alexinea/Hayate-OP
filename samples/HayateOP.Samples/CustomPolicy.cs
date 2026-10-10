using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Samples;

/// <summary>
/// A custom object policy for the tour sample, implementing <see cref="IHayateObjectPolicy{MyPooledObject}"/>
/// to control how pooled objects are created, validated, and reset on acquire/return/destroy.
/// It exists mainly to show that HayateOP lets you hook the full object lifecycle, not just borrow/return.
/// </summary>
public class CustomPolicy : IHayateObjectPolicy<MyPooledObject>
{
    /// <summary>
    /// Creates a brand-new pooled object. Called when the pool needs another instance (warm-up,
    /// scale-up, or a create-on-demand borrow).
    /// </summary>
    /// <returns>A fresh <see cref="MyPooledObject"/> seeded with sentinel Id/Data values.</returns>
    public MyPooledObject Create()
    {
        return new MyPooledObject
        {
            Id = -1,
            Data = "Init"
        };
    }

    /// <summary>
    /// Called when an object is returned to the pool. Resets its mutable state so the next borrower
    /// gets a clean instance.
    /// </summary>
    /// <param name="item">The object being returned.</param>
    /// <returns>
    /// <c>true</c> to return the object to the pool for reuse; <c>false</c> to destroy it immediately.
    /// </returns>
    public bool OnRelease(MyPooledObject item)
    {
        item.Id = 0;
        item.Data = string.Empty;

        // Return true to return the object to the pool; return false to destroy it immediately.
        return true;
    }

    /// <summary>
    /// Validates an object before it is handed out. The tour pool enables validation, so a false
    /// result would cause the object to be destroyed rather than borrowed.
    /// </summary>
    /// <param name="item">The object to validate.</param>
    /// <returns><c>true</c> if the object is fit for use.</returns>
    public bool Validate(MyPooledObject item)
    {
        return true;
    }

    /// <summary>
    /// Called immediately after an object is acquired (before the borrower uses it). The tour policy
    /// has no per-acquire work, so this is a no-op.
    /// </summary>
    /// <param name="item">The acquired object.</param>
    public void OnAcquire(MyPooledObject item)
    {
    }

    /// <summary>
    /// Called when an object becomes idle (parked) in the pool. The tour policy has no passivation work.
    /// </summary>
    /// <param name="item">The object being passivated.</param>
    public void OnPassivate(MyPooledObject item)
    {
    }

    /// <summary>
    /// Called when an object is permanently removed from the pool (eviction, destroy-on-invalid, or
    /// pool disposal). The tour policy has no teardown work.
    /// </summary>
    /// <param name="item">The object being destroyed.</param>
    public void OnDestroy(MyPooledObject item)
    {
    }
}
