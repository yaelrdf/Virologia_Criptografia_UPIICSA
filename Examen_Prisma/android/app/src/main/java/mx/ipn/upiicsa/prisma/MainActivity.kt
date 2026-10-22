package mx.ipn.upiicsa.prisma

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import mx.ipn.upiicsa.prisma.ui.PrismaApp
import mx.ipn.upiicsa.prisma.ui.theme.PrismaTheme

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            PrismaTheme {
                Surface(color = MaterialTheme.colorScheme.background) {
                    PrismaApp(vm)
                }
            }
        }
    }
}
