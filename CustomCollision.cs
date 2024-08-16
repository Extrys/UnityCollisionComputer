using System;
using UnityEngine;

[Serializable]
public struct CustomCollision
{
	public readonly ContactPairHeader m_Header;
	ContactPair m_Pair;
	[SerializeField] Vector3 relVelocity;
	[SerializeField] bool m_Flipped;
	public bool Flipped { get => m_Flipped; private set => m_Flipped = value; }

	public readonly Vector3 impulse => m_Pair.ImpulseSum;
	public readonly Vector3 relativeVelocity => m_Flipped ? -relVelocity : relVelocity;
	public readonly Rigidbody dthisRigidbody => dthisBody as Rigidbody;
	public readonly Rigidbody rigidbody => body as Rigidbody;
	public readonly Component dthisBody => m_Flipped ? m_Header.Body : m_Header.OtherBody;
	public readonly Component body => m_Flipped ? m_Header.OtherBody: m_Header.Body;
	public readonly Collider thisCollider => m_Flipped ? m_Pair.Collider : m_Pair.OtherCollider;
	public readonly Collider collider => m_Flipped ? m_Pair.OtherCollider : m_Pair.Collider;
	public readonly Transform dthisTransform => (dthisRigidbody != null) ? dthisRigidbody.transform : thisCollider.transform;
	public readonly GameObject dthisGameObject => (dthisBody != null) ? dthisBody.gameObject : thisCollider.gameObject;
	public readonly GameObject gameObject => (body != null) ? body.gameObject : collider.gameObject;
	public readonly int dcontactCount => m_Pair.ContactCount;
	public readonly int dThisBodyInstanceId => m_Flipped ? m_Header.BodyInstanceID : m_Header.OtherBodyInstanceID;
	public readonly int dOtherBodyInstanceId => m_Flipped ? m_Header.OtherBodyInstanceID : m_Header.BodyInstanceID;
	public readonly Vector3 dthisBodyVelocity => dthisRigidbody?.velocity?? Vector3.zero;
	public readonly Vector3 dotherBodyVelocity => rigidbody?.velocity?? Vector3.zero;
	internal CustomCollision(in ContactPairHeader header, in ContactPair pair, in bool flipped, in Vector3 relvel)
	{
		m_Header = header;
		m_Pair = pair;
		m_Flipped = false;
		relVelocity = relvel;
	}

	public CustomCollision(Collision collision)
	{
		//access to pair and header of collision using System.Reflections, because they are private
		m_Header = (ContactPairHeader)collision.GetType().GetField("m_Header", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(collision);
		m_Pair = (ContactPair)collision.GetType().GetField("m_Pair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(collision);
		m_Flipped = !(bool)collision.GetType().GetField("m_Flipped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(collision);
		relVelocity = collision.relativeVelocity;
	}

	public unsafe readonly ContactPairPoint GetContact(int index)
	{
		return m_Pair.GetContactPoint(index);
	}

	public CustomCollision AsFlipped()
	{
		m_Flipped = !m_Flipped;
		return this;
	}
	public void SetRelativeVelocity(Vector3 velocity)
	{
		relVelocity = velocity;
	}
	public void ComputeRelativeVelocity()
	{
		relVelocity = dthisBodyVelocity - dotherBodyVelocity;
	}
	public override string ToString()
	{
		string collisionType = m_Pair.IsCollisionEnter ? "ENTER" : m_Pair.IsCollisionExit ? "EXIT" : "STAY";
		return $"This(rb:{dthisBody?.name}, col:{thisCollider?.name}) - Other(rb:{body?.name}, col:{collider?.name}) {collisionType}";
	}
}
