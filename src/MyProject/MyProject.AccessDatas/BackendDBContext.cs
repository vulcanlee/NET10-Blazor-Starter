using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas.Models;

namespace MyProject.AccessDatas;

public partial class BackendDBContext : DbContext
{
    public BackendDBContext()
    {
    }

    public BackendDBContext(DbContextOptions<BackendDBContext> options)
    : base(options)
    {
    }

    public virtual DbSet<MyUser> MyUser { get; set; }
    public virtual DbSet<MyTas> MyTas { get; set; }
    public virtual DbSet<MyTasFile> MyTasFile { get; set; }
    public virtual DbSet<Meeting> Meeting { get; set; }
    public virtual DbSet<MeetingFile> MeetingFile { get; set; }
    public virtual DbSet<Project> Project { get; set; }
    public virtual DbSet<ProjectFile> ProjectFile { get; set; }
    public virtual DbSet<RoleView> RoleView { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            //#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see http://go.microsoft.com/fwlink/?LinkId=723263.
            //                optionsBuilder.UseSqlServer("Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=School");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Chinese_Taiwan_Stroke_CI_AS");

        #region 設定階層級的刪除政策(預設若關聯子資料表有紀錄，父資料表不可強制刪除
        foreach (var relationship in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }
        #endregion

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasMany(x => x.Files)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MyTas>(entity =>
        {
            entity.HasMany(x => x.Files)
                .WithOne(x => x.MyTas)
                .HasForeignKey(x => x.MyTasId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Meeting>(entity =>
        {
            entity.HasMany(x => x.Files)
                .WithOne(x => x.Meeting)
                .HasForeignKey(x => x.MeetingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
