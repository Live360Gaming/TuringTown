/**
 * Subscribes to an emitter for as long as `scene` is running: the handler is called with the scene as `this`
 * and removed again when the scene shuts down. HostStage and PlayerStage expose it as `this.listen(...)`.
 */
export function listen(scene, emitter, event, handler) {
    emitter.on(event, handler, scene);
    scene.events.once('shutdown', () => emitter.off(event, handler, scene));
}
