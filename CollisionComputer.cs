// This code is working with Unity's Physics API, and it's used to handle custom collision events in a more efficient way than Unity's default collision events.
//
// The problem: Unity's OnCollisionEnter and OnCollisionExit are executed per instance, and when 2 objects collide, both objects execute the event, causing duplicated events.
// It makes harder to handle things like particles, sounds, or any other effect that should be executed only once per collision.
//
// The whole file contains all needed code, it could be spread on different files, but i put everything toghether so you can just copy and paste this script in your game
// just implement ICustomCollisionListener interface in your class and use StartListenCollisions and StopListenCollisions extension methods to start and stop listening to collisions, it requires a Rigidbody reference for now
//
// this requires your project to have unsafe code enabled, if youre not comfortable with enabling it in your project, just add this script to an assembly with that setting enabled
//
// Remember to add USE_CONTACTS_API to the project scripting defines for this script to work!
//
// Check more repos in my github: https://github.com/Extrys

using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using System;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

public class CollisionComputer
{
	static CollisionComputer instance; //change this to use injection instead of singleton, keept this way for plug-and-play simplicity on this repo, I HATE MYSELF FOR THIS 🥲
	NativeArray<JobResultStruct> m_ResultsArray;
	NativeArray<int> offsetsArray;
	int m_Count;
	JobHandle m_JobHandle;
	const bool DisableDefaultCollisionEvents =
#if KEEP_OLD_CONTACTS
		false;
#else
		true;
#endif
	// Potential optimization point. TODO: Plan to make a way using DOTS features (or whatever) to make it more efficient withouth the need of this dictionary
	static readonly Dictionary<int, ICustomCollisionListener> collisionListenerMap = new Dictionary<int, ICustomCollisionListener>();

	CollisionComputer()
	{
		if (DisableDefaultCollisionEvents)
		{
			Physics.reuseCollisionCallbacks = false;
			Physics.invokeCollisionCallbacks = false;
		}
		m_ResultsArray = new NativeArray<JobResultStruct>(0, Allocator.Persistent);
		offsetsArray = new NativeArray<int>(0, Allocator.Persistent);
		Physics.ContactEvent += Physics_ContactEvent;
	}

		public static void SusbcribeCollisionListener(Rigidbody rb, ICustomCollisionListener listener)
	{
#if !USE_CONTACTS_API
		return;
#endif
		Initialize();
		collisionListenerMap[rb.GetInstanceID()] = listener;
	}
	public static void UnsubscribeCollisionListener(Rigidbody rb, ICustomCollisionListener listener)
	{
#if !USE_CONTACTS_API
		return;
#endif
		collisionListenerMap.Remove(rb.GetInstanceID());
		if (collisionListenerMap.Count == 0)
			Terminate();
	}
	static readonly UnsafeFieldAccessor unsafeFieldAccessor = new UnsafeFieldAccessor("m_RelativeVelocity");
	static void Initialize()
	{
		if (instance == null)
			instance = new CollisionComputer();
	}

	public static void Terminate() => instance?.Dispose();
	public void Dispose()
	{
#if !USE_CONTACTS_API
		return;
#endif
		instance.m_JobHandle.Complete();
		instance.m_ResultsArray.Dispose();
		instance.offsetsArray.Dispose();
		Physics.ContactEvent -= instance.Physics_ContactEvent;
		instance = null;
	}
	unsafe void OnContactEventProcessed()
	{
#if USE_CONTACTS_API
		if (m_Count <= 0)
			return;

		m_JobHandle.Complete();

		var lastThisInstanceID = -1;
		var lastOtherInstanceID = -1;
		ICustomCollisionListener reusableListenerA = null;
		ICustomCollisionListener reusableListenerB = null;
		for (int i = 0; i < m_Count; i++)
		{
			JobResultStruct result = m_ResultsArray[i];
			CustomCollision collision = result.customCollision;
			var thisInstanceID = collision.dThisBodyInstanceId;
			var otherInstanceID = collision.dOtherBodyInstanceId;
			
			//This reduces the usage of the diccionary, by searching a single time for each pair of bodies, that can contain multiple contacts
			bool isSameBodyPair = lastThisInstanceID == thisInstanceID && lastOtherInstanceID == otherInstanceID;
			if(!isSameBodyPair && (result.isCollisionEnter || result.isCollisionExit))
			{
				lastThisInstanceID = thisInstanceID;
				lastOtherInstanceID = otherInstanceID;
				if (collisionListenerMap.TryGetValue(thisInstanceID, out ICustomCollisionListener listenerA))
					reusableListenerA = listenerA;
				if (collisionListenerMap.TryGetValue(otherInstanceID, out ICustomCollisionListener listenerB))
					reusableListenerB = listenerB;
			}

			if (result.isCollisionEnter)
			{
				//Here you could get the contact points and do whatever you want with them
				//Unlike unity's OnCollisionEnter which makes flippings and other stuff giving duplicated collisions on contact between listeners, this method is more efficient and easier to handle
				//For example, you could spawn a particle or a sound depending on the body, or contact pair youre getting

				//Currently for 100% compatibility with unity's OnCollisionEnter, im flipping the collision and sending it to both listeners, like the original unity's collision events does
				//But you could change this behavior to make it more efficient and avoid duplicated events
				reusableListenerA?.OnCustomCollisionEnter(collision);
				reusableListenerB?.OnCustomCollisionEnter(collision.AsFlipped());
			}
			if (result.isCollisionExit)
			{
				reusableListenerA?.OnCustomCollisionExit(collision);
				reusableListenerB?.OnCustomCollisionExit(collision.AsFlipped());
			}
		}
		m_Count = 0;
#endif
	}
	
	[BurstCompile]
	void Physics_ContactEventBursted(in PhysicsScene scene, in NativeArray<ContactPairHeader>.ReadOnly pairHeaders)
	{
		int pairHeaderCount = pairHeaders.Length;
		int totalContactCount = 0;
		for (int i = 0; i < pairHeaderCount; i++)
			totalContactCount += pairHeaders[i].PairCount;

		if (offsetsArray.Length < pairHeaderCount)
		{
			offsetsArray.Dispose();
			offsetsArray = new NativeArray<int>(Mathf.NextPowerOfTwo(pairHeaderCount), Allocator.Persistent);
		}
		if (m_ResultsArray.Length < totalContactCount)
		{
			m_ResultsArray.Dispose();
			m_ResultsArray = new NativeArray<JobResultStruct>(Mathf.NextPowerOfTwo(totalContactCount), Allocator.Persistent);
		}

		offsetsArray[0] = 0;
		int currentOffset = 0;
		for (int j = 1; j < pairHeaderCount; j++)
			offsetsArray[j] = (currentOffset += pairHeaders[j - 1].PairCount);

		m_Count = totalContactCount;

		ComputeCustomCollisionJob job = new ComputeCustomCollisionJob()
		{
			rbPairs = pairHeaders,
			resultsArray = m_ResultsArray,
			offsets = offsetsArray,
			unsafeFieldAccessor = unsafeFieldAccessor
		};

		m_JobHandle = job.Schedule(pairHeaderCount, default);
	}
	void Physics_ContactEvent(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly pairHeaders)
	{
		Physics_ContactEventBursted(in scene, in pairHeaders);
		OnContactEventProcessed();
	}

	//for future use, is planed to improve this method to be able to get the whole array of All contacts from All pairs directly avoiding the need of the offsetsArray
	//internal unsafe ReadOnlySpan<ContactPair> GetContactPairs(in NativeArray<ContactPairHeader>.ReadOnly pairHeaders, int headerIndex)
	//{
	//	ContactPairHeader firstHeader = pairHeaders[headerIndex];
	//	int pairCount = firstHeader.PairCount;
	//	ref readonly ContactPair contactPair = ref firstHeader.GetContactPair(0);
	//	fixed (ContactPair* ptr = &contactPair)
	//		return new ReadOnlySpan<ContactPair>(ptr, pairCount);
	//}
}

[Serializable]
public struct JobResultStruct
{
	public bool isCollisionEnter;
	public bool isCollisionExit;
	public CustomCollision customCollision;
}

[BurstCompile]
public struct ComputeCustomCollisionJob : IJobParallelFor
{
	[ReadOnly] public NativeArray<ContactPairHeader>.ReadOnly rbPairs;
	[ReadOnly] public NativeArray<int> offsets;
	[NativeDisableParallelForRestriction] public NativeArray<JobResultStruct> resultsArray;
	[ReadOnly] public UnsafeFieldAccessor unsafeFieldAccessor;

	[BurstCompile]
	public unsafe void Execute(int index)
	{
		ContactPairHeader rbPair = rbPairs[index];
		Vector3 relativeVelocity = unsafeFieldAccessor.GetPrivateFieldValue(&rbPair);
		int offset = offsets[index];
		for (int i = 0; i < rbPair.PairCount; i++)
		{
			ref readonly ContactPair contactPair = ref rbPair.GetContactPair(i);
			resultsArray[offset + i] = new JobResultStruct()
			{
				isCollisionEnter = contactPair.IsCollisionEnter,
				isCollisionExit = contactPair.IsCollisionExit,
				customCollision = new CustomCollision(in rbPair, in contactPair, false, in relativeVelocity)
			};
		}
	}
}

public interface ICustomCollisionListener
{
#if USE_CONTACTS_API
	void OnCustomCollisionEnter(in CustomCollision collision);
	void OnCustomCollisionExit(in CustomCollision collision);
#endif
}

public static class CustomCollisionListenerExtensions
{
	public static void StartListenCollisions(this ICustomCollisionListener listener, Rigidbody rb) => CollisionComputer.SusbcribeCollisionListener(rb, listener);
	public static void StopListenCollisions(this ICustomCollisionListener listener, Rigidbody rb) => CollisionComputer.UnsubscribeCollisionListener(rb, listener);
}


// To access relativeVelocity field of ContactPairHeader in a fast way (can me more efficient than direct access in some cases)
// Could use generics to make it more reusable, but it's not necessary for this case and it would make the code more complex and less performant
public unsafe readonly struct UnsafeFieldAccessor
{
	readonly int _valueOffset;
	public UnsafeFieldAccessor(string fieldName)
	{
		var fieldInfo = typeof(ContactPairHeader).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
		_valueOffset = (int)Marshal.OffsetOf<ContactPairHeader>(fieldInfo.Name);

		unsafe //WarmUp
		{
			var tempInstance = new ContactPairHeader();
			var stop = Stopwatch.StartNew();
			GetPrivateFieldValue(&tempInstance); // Forces JIT Compilation
			stop.Stop();
			double t = stop.Elapsed.TotalMilliseconds;
			UnityEngine.Debug.Log($"WarmUp UnsafeFieldAccessor for ContactPairHeader took: {t} ms");
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector3 GetPrivateFieldValue(in ContactPairHeader* instance) => *(Vector3*)((byte*)instance + _valueOffset);
}

