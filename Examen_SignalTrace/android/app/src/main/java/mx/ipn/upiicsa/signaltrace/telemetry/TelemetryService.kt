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
 * Foreground service of type "specialUse" (sensor telemetry): keeps sampling and sending with the screen locked.
 * It is started only from the visible UI, shows a persistent notification with a Stop action and holds a partial wake lock.
 */
class TelemetryService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            TelemetryEngine.stop()
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        createChannel()
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification("Iniciando…"), type)
        } catch (e: Exception) {
            // Android 12+ forbids starting from the background (e.g. a sticky restart): ask the user to reopen the app.
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
                if (!s.running) stopSelf()
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        scope.cancel()
        wakeLock?.takeIf { it.isHeld }?.release()
        super.onDestroy()
    }

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Telemetría", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Aviso permanente mientras SignalTrace envía telemetría"
        })
    }

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
        const val ACTION_STOP = "mx.ipn.upiicsa.signaltrace.STOP"

        fun start(context: Context) = ContextCompat.startForegroundService(context, Intent(context, TelemetryService::class.java))
        fun stop(context: Context) = context.startService(Intent(context, TelemetryService::class.java).setAction(ACTION_STOP))
    }
}
