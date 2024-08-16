// This code is working with Unity's Physics API, and it's used to handle custom collision events in a more efficient way than Unity's default collision events.
//
// The problem: Unity's OnCollisionEnter and OnCollisionExit are executed per instance, and when 2 objects collide, both objects execute the event, causing duplicated events.
// It makes harder to handle things like particles, sounds, or any other effect that should be executed only once per collision.
//
// The whole file contains all needed code, it could be spread on different files, but i put everything toghether so you can just copy and paste this script in your game
//   just implement ICustomCollisionListener interface in your class and use StartListenCollisions and StopListenCollisions extension methods
//   to start and stop listening to collisions, it requires a Rigidbody reference for now
//
// This requires your project to have unsafe code enabled, if youre not comfortable with enabling it in your project, just add this script to an assembly with that setting enabled
//
// Remember to add USE_CONTACTS_API to the project scripting defines for this script to work!
//
// I would consider creating a package for this if it gets enough attention, but for now, it's just a script that you can use in your project
//
// Check more repos in my github: https://github.com/Extrys

// This script is also compatible with profiler modules, you can see the collisions processing time in the profiler window,
//   just enable the profiler module in the window settings, for that, you need to Install the com.unity.profiling.core package in your project
//   once installed just add USE_PROFILER_MODULE to the project scripting defines

#if USE_PROFILER_MODULE && DEBUG
using Unity.Profiling;
#endif
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
using UnityEngine.Profiling;


public struct CollisionComputer
{
	static bool initialized;
	static NativeArray<JobResultStruct> m_ResultsArray;
	static NativeArray<int> offsetsArray;
	static NativeArray<int> count, headerCount, nonStayContactCount;
	static int totalContactCount, totalHeaderCount, totalNonStayContactCount;
	static JobHandle m_JobHandle;
	static UnsafeFieldAccessor unsafeFieldAccessor;
	static Stopwatch eventsTime, preprocessTime;
	const bool DisableDefaultCollisionEvents =
#if KEEP_OLD_CONTACTS
		false;
#else
		true;
#endif
	// Potential optimization point. TODO: Plan to make a way using DOTS features (or whatever) to make it more efficient withouth the need of this dictionary
	static readonly Dictionary<int, ICustomCollisionListener> collisionListenerMap = new Dictionary<int, ICustomCollisionListener>();

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

	static void Initialize()
	{
		if (initialized)
			return;

#if USE_PROFILER_MODULE && DEBUG
		eventsTime = new Stopwatch();
		preprocessTime = new Stopwatch();
#endif

        if (DisableDefaultCollisionEvents)
		{
			Physics.reuseCollisionCallbacks = false;
			Physics.invokeCollisionCallbacks = false;
		}
		m_ResultsArray = new NativeArray<JobResultStruct>(0, Allocator.Persistent);
		offsetsArray = new NativeArray<int>(0, Allocator.Persistent);
		count = new NativeArray<int>(1, Allocator.Persistent);
		headerCount = new NativeArray<int>(1, Allocator.Persistent);
		nonStayContactCount = new NativeArray<int>(1, Allocator.Persistent);
		unsafeFieldAccessor = new UnsafeFieldAccessor("m_RelativeVelocity");
		Physics.ContactEvent += Physics_ContactEvent;
		initialized = true;
	}

	public static void Terminate()
	{
#if !USE_CONTACTS_API
		return;
#endif
		m_JobHandle.Complete();
		m_ResultsArray.Dispose();
		offsetsArray.Dispose();
		Physics.ContactEvent -= Physics_ContactEvent;
		initialized = false;
	}

	public static unsafe void OnContactEventProcessed()
	{
#if USE_CONTACTS_API


		if (totalContactCount <= 0)
			return;

		m_JobHandle.Complete();
		totalNonStayContactCount = nonStayContactCount[0];
		for (int i = 0; i < totalNonStayContactCount; i++)
		{
			JobResultStruct result = m_ResultsArray[i];
			CustomCollision collision = result.customCollision;
			var thisInstanceID = collision.dThisBodyInstanceId;
			var otherInstanceID = collision.dOtherBodyInstanceId;

			if (result.isCollisionEnter)
			{
				//Here you could get the contact points and do whatever you want with them
				//Unlike unity's OnCollisionEnter which makes flippings and other stuff giving duplicated collisions on contact between listeners, this method is more efficient and easier to handle
				//For example, you could spawn a particle or a sound depending on the body, or contact pair youre getting

				//Currently for 100% compatibility with unity's OnCollisionEnter, im flipping the collision and sending it to both listeners, like the original unity's collision events does
				//But you could change this behavior to make it more efficient and avoid duplicated events
				if (collisionListenerMap.TryGetValue(thisInstanceID, out ICustomCollisionListener listenerA))
					listenerA?.OnCustomCollisionEnter(collision);
				if (collisionListenerMap.TryGetValue(otherInstanceID, out ICustomCollisionListener listenerB))
					listenerB?.OnCustomCollisionEnter(collision.AsFlipped());
			}
			if (result.isCollisionExit)
			{
				if (collisionListenerMap.TryGetValue(thisInstanceID, out ICustomCollisionListener listenerA))
					listenerA?.OnCustomCollisionExit(collision);
				if (collisionListenerMap.TryGetValue(otherInstanceID, out ICustomCollisionListener listenerB))
					listenerB?.OnCustomCollisionExit(collision.AsFlipped());
			}
		}
		totalContactCount = 0;
#endif
	}

	static void Physics_ContactEvent(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly pairHeaders)
	{
#if USE_PROFILER_MODULE && DEBUG
		preprocessTime.Restart();
#endif
        Physics_ContactEventJobs(in scene, in pairHeaders);
#if USE_PROFILER_MODULE && DEBUG
		preprocessTime.Stop();
		CustomCollisionStatistics.preprocessConsuption.Value += preprocessTime.Elapsed.TotalMilliseconds * 1000000d;//ms to ns
		eventsTime.Restart();
#endif
        OnContactEventProcessed();
#if USE_PROFILER_MODULE && DEBUG
		eventsTime.Stop();
		CustomCollisionStatistics.collisionConsuption.Value += eventsTime.Elapsed.TotalMilliseconds * 1000000d;//ms to ns
#endif
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
    static void Physics_ContactEventJobs(in PhysicsScene scene, in NativeArray<ContactPairHeader>.ReadOnly pairHeaders)
	{
		//Get total contact count and total header count
		new ContactCounterJob()
		{
			pairHeaders = pairHeaders,
			contactCountResult = count,
			headerCountResult = headerCount
		}.Schedule().Complete();
		totalContactCount = count[0];
		totalHeaderCount = headerCount[0];

		// Resize the arrays for the unroll job
		if (offsetsArray.Length < totalHeaderCount)
		{
			offsetsArray.Dispose();
			offsetsArray = new NativeArray<int>(Mathf.NextPowerOfTwo(totalHeaderCount), Allocator.Persistent);
		}
		if (m_ResultsArray.Length < totalContactCount)
		{
			m_ResultsArray.Dispose();
			m_ResultsArray = new NativeArray<JobResultStruct>(Mathf.NextPowerOfTwo(totalContactCount), Allocator.Persistent);
		}

		//prepare the offsets array for the unroller job
		JobHandle unrollerHandle = new ContactHeaderUnrollerJob()
		{
			pairHeaders = pairHeaders,
			pairHeaderCount = totalHeaderCount,
			offsetsArrayResult = offsetsArray
		}.Schedule();

		//Fill the unrolled contacts
		JobHandle fillUnrolledJob = new FillUnrolledContactsJob()
		{
			rbPairs = pairHeaders,
			resultsArray = m_ResultsArray,
			offsets = offsetsArray,
			unsafeFieldAccessor = unsafeFieldAccessor
		}.Schedule(totalHeaderCount, default, unrollerHandle);

		//Post process the unrolled contacts, ordering them by pair index and counting non stay contacts to process only the necessary ones
		m_JobHandle = new PostProcessUnrolledContactsJob() 
		{ 
			results = m_ResultsArray, 
			nonStayContactCountResult = nonStayContactCount 
		}.Schedule(fillUnrolledJob);
	}
}

[Serializable]
public struct JobResultStruct : IComparable<JobResultStruct>
{
	public bool isCollisionEnter;
	public bool isCollisionExit;
	public CustomCollision customCollision;
	// results comes already sorted by pair index, this is just to move all stay contacts to the end of the array without losing the usable pair optimized order
	public int pairIndex;

	public int CompareTo(JobResultStruct other)
	{
		bool isNotStay = isCollisionEnter || isCollisionExit;
		float score = isNotStay ? (pairIndex - (isCollisionExit ? .5f : 0f)) : -1f;
		float otherScore = other.isCollisionEnter || other.isCollisionExit ? other.pairIndex : -1f;
		return (score < otherScore ? 1 : score > otherScore ? -1 : 0);
	}
}


[BurstCompile]
public struct ContactCounterJob : IJob
{
	[ReadOnly] public NativeArray<ContactPairHeader>.ReadOnly pairHeaders;
	public NativeArray<int> contactCountResult, headerCountResult;
	public void Execute()
	{
		int pairHeaderCount = pairHeaders.Length;
		int totalContactCount = 0;
		for (int i = 0; i < pairHeaderCount; i++)
			totalContactCount += pairHeaders[i].PairCount;
		headerCountResult[0] = pairHeaderCount;
		contactCountResult[0] = totalContactCount;
	}

}
[BurstCompile]
public struct ContactHeaderUnrollerJob : IJob
{
	[ReadOnly] public NativeArray<ContactPairHeader>.ReadOnly pairHeaders;
	public int pairHeaderCount;
	public NativeArray<int> offsetsArrayResult;
	public void Execute()
	{
		offsetsArrayResult[0] = 0;
		int currentOffset = 0;
		for (int j = 1; j < pairHeaderCount; j++)
			offsetsArrayResult[j] = (currentOffset += pairHeaders[j - 1].PairCount);
	}
}

[BurstCompile]
public struct PostProcessUnrolledContactsJob : IJob
{
	public NativeArray<JobResultStruct> results;
	[WriteOnly] public NativeArray<int> nonStayContactCountResult;
	public void Execute()
	{
		results.Sort();
		int nonStayCount = 0;
		for (int i = 0; i < results.Length; i++)
			if (results[i].isCollisionEnter || results[i].isCollisionExit)
				nonStayCount++;
		nonStayContactCountResult[0] = nonStayCount;
	}
}




[BurstCompile]
public struct FillUnrolledContactsJob : IJobParallelFor
{
	[ReadOnly] public NativeArray<ContactPairHeader>.ReadOnly rbPairs;
	[ReadOnly] public NativeArray<int> offsets;
	[NativeDisableParallelForRestriction] public NativeArray<JobResultStruct> resultsArray;
	[ReadOnly] public UnsafeFieldAccessor unsafeFieldAccessor;

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
				customCollision = new CustomCollision(in rbPair, in contactPair, false, in relativeVelocity),
				pairIndex = index
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
			CustomDebug.Log($"WarmUp UnsafeFieldAccessor for ContactPairHeader took: {t} ms");
		}
	}
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector3 GetPrivateFieldValue(in ContactPairHeader* instance) => *(Vector3*)((byte*)instance + _valueOffset);
}


#if USE_PROFILER_MODULE && DEBUG
public static class CustomCollisionStatistics
{
	public const string ContactEventsProcessing = "Process";
	public readonly static ProfilerCategory processCategory = new ProfilerCategory("OnContactEventProcessed", ProfilerCategoryColor.Scripts);
	public static readonly ProfilerCounterValue<double> collisionConsuption =
	    new ProfilerCounterValue<double>(processCategory, ContactEventsProcessing, ProfilerMarkerDataUnit.TimeNanoseconds,
		  ProfilerCounterOptions.FlushOnEndOfFrame | ProfilerCounterOptions.ResetToZeroOnFlush);

	public const string ContactEventsPreprocessing = "Preprocess";
	public readonly static ProfilerCategory preprocessCategory = new ProfilerCategory("OnContactEventPreprocessed", ProfilerCategoryColor.Scripts);
	public static readonly ProfilerCounterValue<double> preprocessConsuption =
	    new ProfilerCounterValue<double>(preprocessCategory, ContactEventsPreprocessing, ProfilerMarkerDataUnit.TimeNanoseconds,
		  ProfilerCounterOptions.FlushOnEndOfFrame | ProfilerCounterOptions.ResetToZeroOnFlush);
}


#if UNITY_EDITOR
[Unity.Profiling.Editor.ProfilerModuleMetadata("Collision Computer")]
public class CollisionComputerProfileModule : Unity.Profiling.Editor.ProfilerModule
{
	static readonly Unity.Profiling.Editor.ProfilerCounterDescriptor[] k_Counters = new Unity.Profiling.Editor.ProfilerCounterDescriptor[]
	{
	    new Unity.Profiling.Editor.ProfilerCounterDescriptor(CustomCollisionStatistics.ContactEventsProcessing, CustomCollisionStatistics.processCategory),
	    new Unity.Profiling.Editor.ProfilerCounterDescriptor(CustomCollisionStatistics.ContactEventsPreprocessing, CustomCollisionStatistics.preprocessCategory)
	};
	public CollisionComputerProfileModule() : base(k_Counters, Unity.Profiling.Editor.ProfilerModuleChartType.StackedTimeArea) { }
}
#endif //UNITY_EDITOR
#endif //USE_PROFILER_MODULE && DEBUG
