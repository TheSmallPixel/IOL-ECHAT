using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class CekWrapCutover : Migration
    {
        
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ordine: prima i dati cifrati con le vecchie CEK, poi le CEK stesse.
            migrationBuilder.Sql("DELETE FROM `Messages`;");        // include i tombstone (righe Messages)
            migrationBuilder.Sql("DELETE FROM `KeyEnvelopes`;");    // CEK legacy in chiaro
            migrationBuilder.Sql("DELETE FROM `ChainBoundaries`;"); // riferivano lo storico cancellato
            // Reset dei contatori/lease di sequenza: lo storico è azzerato, le conversazioni ripartono da seq 1.
            migrationBuilder.Sql("DELETE FROM `SeqCounters`;");
            migrationBuilder.Sql("DELETE FROM `SeqLeases`;");
            // Job di migrazione (FullReencrypt) ormai orfani dello storico cancellato.
            migrationBuilder.Sql("DELETE FROM `MigrationJobs`;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
