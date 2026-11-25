/**
 * Subscribes to an emitter for as long as `scene` is running: the handler is called with the scene as `this`
 * and removed again when the scene shuts down. HostStage and PlayerStage expose it as `this.listen(...)`.
 *
 * Removing a listener is not enough on its own: an emitter still calls everyone who was subscribed when the event began,
 * and the director closes the old screen inside the `state` event. So a closed screen is also ignored.
 */
export function listen(scene, emitter, event, handler) {
    let open = true;
    const whileOpen = (...args) => {
        if (open) handler.apply(scene, args);
    };
    emitter.on(event, whileOpen);
    scene.events.once('shutdown', () => {
        open = false;
        emitter.off(event, whileOpen);
    });
}
