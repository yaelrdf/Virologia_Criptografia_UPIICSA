package mx.ipn.upiicsa.signaltrace.telemetry

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import mx.ipn.upiicsa.signaltrace.MainActivity
import mx.ipn.upiicsa.signaltrace.R
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Servicio en primer plano de tipo «specialUse» (telemetría de sensores): sigue muestreando y enviando con la pantalla
 * bloqueada. Solo se inicia desde la interfaz visible, muestra una notificación permanente con la acción Detener y
 * mantiene un wake lock parcial para que la CPU no se duerma.
 */
class TelemetryService : Service() {
    /** Bloqueo que mantiene la CPU despierta mientras se transmite. */
    private var wakeLock: PowerManager.WakeLock? = null
    /** Ámbito de corrutinas del servicio, para actualizar la notificación. */
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /** No admite enlace: se controla con start y stop. */
    override fun onBind(intent: Intent?): IBinder? = null

    /**
     * Arranca o detiene el servicio. Al arrancar: crea el canal, pasa a primer plano con la notificación, toma el
     * wake lock (máximo 12 h), inicia el motor y actualiza la notificación cada vez que cambia el estado.
     */
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            TelemetryEngine.stop()
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        createChannel()
        // Android 14+ exige declarar el tipo de servicio en primer plano.
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification("Iniciando…"), type)
        } catch (e: Exception) {
            // Android 12+ prohíbe iniciar desde segundo plano (por ejemplo, en un reinicio automático): se pide al usuario reabrir la app.
            notifyReopen()
            stopSelf()
            return START_NOT_STICKY
        }
        wakeLock = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SignalTrace::telemetry").apply { acquire(12 * 60 * 60 * 1000L) }
        TelemetryEngine.start(this)
        scope.launch {
            val fmt = SimpleDateFormat("HH:mm:ss", Locale.US)
            TelemetryEngine.state.collectLatest { s ->
                val last = s.lastSendAt?.let { "último envío ${fmt.format(Date(it))}" } ?: s.status
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification("${s.status} · $last · ${s.ok} enviados"))
                // Si el motor se detuvo (por ejemplo, token revocado), el servicio también termina.
                if (!s.running) stopSelf()
            }
        }
        return START_STICKY
    }

    /** Cancela las corrutinas y libera el wake lock. */
    override fun onDestroy() {
        scope.cancel()
        wakeLock?.takeIf { it.isHeld }?.release()
        super.onDestroy()
    }

    /** Canal de notificaciones de baja importancia (sin sonido). */
    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Telemetría", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Aviso permanente mientras SignalTrace envía telemetría"
        })
    }

    /** Notificación permanente: al tocarla abre la app y tiene el botón Detener. */
    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, TelemetryService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_signal)
            .setContentTitle("SignalTrace enviando telemetría")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(0, "Detener", stop)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    /** Aviso de que el servicio se detuvo y hay que abrir la app para reanudar. */
    private fun notifyReopen() {
        createChannel()
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID + 1,
            NotificationCompat.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_stat_signal).setContentTitle("SignalTrace se detuvo")
                .setContentText("Abre la app para reanudar la telemetría").setContentIntent(open).setAutoCancel(true).build())
    }

    companion object {
        private const val CHANNEL = "telemetry"
        private const val NOTIFICATION_ID = 42
        /** Acción del intent que detiene el servicio. */
        const val ACTION_STOP = "mx.ipn.upiicsa.signaltrace.STOP"

        /** Inicia el servicio en primer plano. */
        fun start(context: Context) = ContextCompat.startForegroundService(context, Intent(context, TelemetryService::class.java))
        /** Pide al servicio que se detenga. */
        fun stop(context: Context) = context.startService(Intent(context, TelemetryService::class.java).setAction(ACTION_STOP))
    }
}
