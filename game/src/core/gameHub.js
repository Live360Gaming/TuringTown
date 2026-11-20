import {HubConnectionBuilder, LogLevel} from '@microsoft/signalr';

/**
 * Connects to the game hub and joins a room.
 *  - `join(connection)` invokes JoinAsHost / JoinAsPlayer. It runs again after an automatic reconnect,
 *    because the server forgets group membership when a connection drops.
 *  - `on` maps hub event names to handlers. They are registered before the connection starts so no event is missed.
 * Resolves with `{connection, result}` (result is whatever `join` returned).
 */
export async function connectGameHub({join, on = {}}) {
    const connection = new HubConnectionBuilder()
        .withUrl(`${window.location.origin}/hubs/game`)
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

    for (const [name, handler] of Object.entries(on)) connection.on(name, handler);
    connection.onreconnected(() => join(connection).catch(() => {}));

    await connection.start();
    try {
        const result = await join(connection);
        return {connection, result};
    } catch (err) {
        await connection.stop();
        throw err;
    }
}

/** SignalR wraps server HubException messages in boilerplate; return just the readable part. */
export function hubErrorMessage(err) {
    const msg = err?.message ?? String(err);
    const marker = 'HubException: ';
    const i = msg.lastIndexOf(marker);
    return i >= 0 ? msg.slice(i + marker.length) : msg;
}
