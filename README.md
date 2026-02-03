The problem: Unity's OnCollisionEnter and OnCollisionExit are executed per instance, and when 2 objects collide, both objects execute the event, causing duplicated events.
It makes harder to handle things like particles, sounds, or any other effect that should be executed only once per collision.

This code is working with Unity's Physics API, and it's used to handle custom collision events in a more efficient way than Unity's default collision events.  
The whole file contains all needed code to make it work
just implement ICustomCollisionListener interface in your class and use StartListenCollisions and StopListenCollisions extension methods to start and stop listening to collisions. it requires a Rigidbody reference in each listener for now.


NOTICE:
Im working on a set of tools called *Domicontacts* which will be a subset of features from a package i will publish soon called *Dominode* both will be separated and Domicontacts will include as it develops a reworked version of this repo, more performant and more controled api
